using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.RevitOperations.CreateElements;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.RevitOperations.Shared
{
    /// <summary>
    /// Vertical placement for ceilings: flush to wall top (painted slab) vs dropped false ceiling,
    /// always clamped above wall-hosted inserts and openings.
    /// </summary>
    public enum RevitCeilingVerticalKind
    {
        /// <summary>Default — ceiling below wall top by a configurable drop, still above openings.</summary>
        FalseCeiling,

        /// <summary>Underside aligned to wall tops (laje pintada), still above openings if needed.</summary>
        SlabPainted
    }

    public static class RevitCeilingVerticalPlacement
    {
        private const double ClearanceAboveOpeningMeters = 0.02;
        private const double BboxPadMeters = 0.45;
        private const double DefaultFalseCeilingDropMeters = 0.30;

        /// <summary>Parse JSON fields; default is <see cref="RevitCeilingVerticalKind.FalseCeiling"/>.</summary>
        public static RevitCeilingVerticalKind ParseKind(JObject op)
        {
            var raw = op["ceilingKind"]?.ToString()
                      ?? op["ceilingVerticalKind"]?.ToString()
                      ?? op["teto"]?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return RevitCeilingVerticalKind.FalseCeiling;
            }

            var s = raw.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty);
            return s switch
            {
                "slabpainted" or "paintedslab" or "laje" or "lajepintada" or "flush" or "coladoalaje" or "totop" =>
                    RevitCeilingVerticalKind.SlabPainted,
                "falseceiling" or "falseteto" or "tetofalso" or "falso" or "suspended" or "drop" =>
                    RevitCeilingVerticalKind.FalseCeiling,
                _ => RevitCeilingVerticalKind.FalseCeiling
            };
        }

        public static double ReadFalseCeilingDropMeters(JObject op, PluginSettings? settings)
        {
            var fromOp = op["falseCeilingDropMeters"]?.Value<double?>()
                         ?? op["falseCeilingDrop"]?.Value<double?>()
                         ?? op["quedaTetoFalsoMetros"]?.Value<double?>();
            if (fromOp.HasValue && fromOp.Value > 1e-6)
            {
                return fromOp.Value;
            }

            if (settings != null && settings.DefaultFalseCeilingDropMeters > 1e-6)
            {
                return settings.DefaultFalseCeilingDropMeters;
            }

            return DefaultFalseCeilingDropMeters;
        }

        /// <summary>Apply height offset from level after ceiling exists; no-op if no usable walls.</summary>
        public static void ApplyAfterCreate(
            Document doc,
            Ceiling ceiling,
            Level level,
            IReadOnlyList<Wall>? wallsHint,
            RevitCeilingVerticalKind kind,
            double falseCeilingDropMeters,
            StringBuilder? log)
        {
            try
            {
                doc.Regenerate();
            }
            catch
            {
                // continue; bbox may still work
            }

            var walls = wallsHint is { Count: > 0 }
                ? wallsHint.Distinct().ToList()
                : CollectWallsNearCeilingPlan(doc, ceiling, level.Id);
            if (walls.Count == 0)
            {
                log?.AppendLine(
                    "ceiling_vertical: no nearby walls on level for height rules; leaving Revit default offset.");
                return;
            }

            if (!TryComputeHeightAboveLevelFeet(
                    doc,
                    level,
                    walls,
                    kind,
                    falseCeilingDropMeters,
                    out var offsetFeet,
                    out var detail))
            {
                log?.AppendLine("ceiling_vertical: could not compute offset; leaving default.");
                return;
            }

            var p = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
            if (p == null || p.IsReadOnly)
            {
                log?.AppendLine("ceiling_vertical: CEILING_HEIGHTABOVELEVEL_PARAM missing or read-only.");
                return;
            }

            p.Set(offsetFeet);
            log?.AppendLine("ceiling_vertical " + detail + " offsetAboveLevelFt=" + offsetFeet.ToString("0.###"));
        }

        public static bool TryApplyToExisting(
            Document doc,
            Ceiling ceiling,
            RevitCeilingVerticalKind kind,
            double falseCeilingDropMeters,
            out string message)
        {
            message = string.Empty;
            if (doc.GetElement(ceiling.LevelId) is not Level level)
            {
                message = "Ceiling has no valid level.";
                return false;
            }

            try
            {
                doc.Regenerate();
            }
            catch
            {
                // ignore
            }

            var walls = CollectWallsNearCeilingPlan(doc, ceiling, level.Id);
            if (walls.Count == 0)
            {
                message = "No nearby walls on the same level to derive heights.";
                return false;
            }

            if (!TryComputeHeightAboveLevelFeet(
                    doc,
                    level,
                    walls,
                    kind,
                    falseCeilingDropMeters,
                    out var offsetFeet,
                    out var detail))
            {
                message = "Could not compute ceiling offset.";
                return false;
            }

            var p = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
            if (p == null || p.IsReadOnly)
            {
                message = "Ceiling height parameter is not writable.";
                return false;
            }

            p.Set(offsetFeet);
            message = detail + " (ft above level: " + offsetFeet.ToString("0.###") + ")";
            return true;
        }

        public static List<Wall> CollectWallsNearRoomPlan(Document doc, Room room, Level level)
        {
            var bb = room.get_BoundingBox(null);
            if (bb == null)
            {
                return CollectAllWallsOnLevel(doc, level.Id);
            }

            return CollectWallsIntersectingExpandedBbox(doc, level.Id, bb);
        }

        public static List<Wall> CollectWallsNearCurveLoop(Document doc, ElementId levelId, CurveLoop loop, double zWork)
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (var c in loop)
            {
                try
                {
                    foreach (XYZ q in c.Tessellate())
                    {
                        minX = Math.Min(minX, q.X);
                        minY = Math.Min(minY, q.Y);
                        maxX = Math.Max(maxX, q.X);
                        maxY = Math.Max(maxY, q.Y);
                    }
                }
                catch
                {
                    // skip curve
                }
            }

            if (minX > maxX)
            {
                return new List<Wall>();
            }

            var pad = RevitWallCreationOps.MetersToFeet(BboxPadMeters);
            var bb = new BoundingBoxXYZ
            {
                Min = new XYZ(minX - pad, minY - pad, zWork - pad),
                Max = new XYZ(maxX + pad, maxY + pad, zWork + pad)
            };
            return CollectWallsIntersectingExpandedBbox(doc, levelId, bb);
        }

        private static List<Wall> CollectWallsNearCeilingPlan(Document doc, Ceiling ceiling, ElementId levelId)
        {
            var bb = ceiling.get_BoundingBox(null);
            if (bb == null)
            {
                return new List<Wall>();
            }

            var pad = RevitWallCreationOps.MetersToFeet(BboxPadMeters);
            var e = new BoundingBoxXYZ
            {
                Min = new XYZ(bb.Min.X - pad, bb.Min.Y - pad, bb.Min.Z - pad),
                Max = new XYZ(bb.Max.X + pad, bb.Max.Y + pad, bb.Max.Z + pad)
            };
            return CollectWallsIntersectingExpandedBbox(doc, levelId, e);
        }

        private static List<Wall> CollectWallsIntersectingExpandedBbox(Document doc, ElementId levelId, BoundingBoxXYZ box)
        {
            var walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w.LevelId == levelId)
                .ToList();

            var list = new List<Wall>();
            foreach (var w in walls)
            {
                var wbb = w.get_BoundingBox(null);
                if (wbb == null)
                {
                    continue;
                }

                if (!BoxesOverlapXY(wbb, box))
                {
                    continue;
                }

                list.Add(w);
            }

            return list;
        }

        private static bool BoxesOverlapXY(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return a.Max.X >= b.Min.X && a.Min.X <= b.Max.X && a.Max.Y >= b.Min.Y && a.Min.Y <= b.Max.Y;
        }

        private static List<Wall> CollectAllWallsOnLevel(Document doc, ElementId levelId)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w.LevelId == levelId)
                .ToList();
        }

        private static bool TryComputeHeightAboveLevelFeet(
            Document doc,
            Level level,
            IReadOnlyList<Wall> walls,
            RevitCeilingVerticalKind kind,
            double falseCeilingDropMeters,
            out double offsetFeet,
            out string detail)
        {
            offsetFeet = 0;
            detail = string.Empty;
            var levelZ = level.Elevation;
            var maxWallTop = levelZ;
            var maxOpeningTop = levelZ;

            foreach (var wall in walls)
            {
                var baseZ = levelZ + (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0.0);
                var h = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble();
                if (h is double hh && hh > 1e-9)
                {
                    maxWallTop = Math.Max(maxWallTop, baseZ + hh);
                }

                maxOpeningTop = Math.Max(maxOpeningTop, GetMaxInsertTopZ(doc, wall));
            }

            var eps = RevitWallCreationOps.MetersToFeet(ClearanceAboveOpeningMeters);
            var minUnderside = Math.Max(maxWallTop, maxOpeningTop + eps);
            var dropFt = RevitWallCreationOps.MetersToFeet(Math.Max(0.0, falseCeilingDropMeters));

            double targetUnderside;
            if (kind == RevitCeilingVerticalKind.SlabPainted)
            {
                targetUnderside = minUnderside;
                detail = "kind=slab_painted";
            }
            else
            {
                targetUnderside = Math.Max(maxWallTop - dropFt, maxOpeningTop + eps);
                detail = "kind=false_ceiling dropM=" + falseCeilingDropMeters.ToString("0.##");
            }

            offsetFeet = targetUnderside - levelZ;
            return offsetFeet > -1e-6;
        }

        private static double GetMaxInsertTopZ(Document doc, Wall wall)
        {
            var maxZ = double.MinValue;
            IList<ElementId>? inserts = null;
            try
            {
                inserts = wall.FindInserts(true, false, false, true);
            }
            catch
            {
                return maxZ;
            }

            if (inserts == null)
            {
                return maxZ;
            }

            foreach (var id in inserts)
            {
                var el = doc.GetElement(id);
                if (el == null)
                {
                    continue;
                }

                var bb = el.get_BoundingBox(null);
                if (bb != null)
                {
                    maxZ = Math.Max(maxZ, bb.Max.Z);
                }
            }

            return maxZ;
        }
    }
}
