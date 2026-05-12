using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitSketchPoC.Chat.Services
{
    /// <summary>
    /// Builds compact JSON/text snapshots of the active document and selection for LLM chat context.
    /// </summary>
    public static class RevitChatContextBuilder
    {
        private const int MaxGeometryWalls = 150;
        private const int MaxGeometryDoors = 100;
        private const int MaxGeometryWindows = 100;
        private const int MaxGeometryRooms = 50;
        private const int MaxFootprintHintFloors = 24;
        private const int MaxFootprintHintCeilings = 24;
        private const int MaxSuggestedWallIdsPerSlab = 48;
        private const double FootprintHintBboxPadMeters = 0.75;

        private static readonly BuiltInCategory[] CountCategories =
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Rooms,
            BuiltInCategory.OST_Furniture,
            BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFraming
        };

        /// <summary>Project-wide snapshot (no geometry dump).</summary>
        public static string BuildProjectSnapshot(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Take(20)
                .Select(l => new Dictionary<string, object?>
                {
                    ["name"] = l.Name,
                    ["elevation"] = FormatLength(doc, l.Elevation)
                })
                .ToList();

            var counts = new Dictionary<string, int>();
            foreach (var bic in CountCategories)
            {
                var n = CountCategory(doc, bic);
                if (n > 0)
                {
                    counts[bic.ToString()] = n;
                }
            }

            var projectInfo = doc.ProjectInformation;
            var projectNumber = projectInfo?.get_Parameter(BuiltInParameter.PROJECT_NUMBER)?.AsString();
            var projectName = projectInfo?.get_Parameter(BuiltInParameter.PROJECT_NAME)?.AsString();

            var payload = new Dictionary<string, object?>
            {
                ["documentTitle"] = doc.Title,
                ["documentPath"] = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
                ["displayUnitSystem"] = doc.DisplayUnitSystem.ToString(),
                ["activeView"] = view == null
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["name"] = view.Name,
                        ["viewType"] = view.ViewType.ToString(),
                        ["discipline"] = view.Discipline.ToString()
                    },
                ["projectInfo"] = string.IsNullOrEmpty(projectName) && string.IsNullOrEmpty(projectNumber)
                    ? null
                    : new Dictionary<string, string?>
                    {
                        ["projectName"] = projectName,
                        ["projectNumber"] = projectNumber
                    },
                ["levels"] = levels,
                ["elementCountsByBuiltInCategory"] = counts,
                ["note"] = "Counts are non-type instances only. Use element ids from selection context when provided."
            };

            payload["planGeometryInActiveView"] = TryBuildPlanGeometryInActiveView(uidoc);
            payload["namedTypesForRevitOps"] = BuildNamedTypesForRevitOps(doc);
            payload["footprintRepairHints"] = BuildFootprintRepairHints(doc);
            payload["revitOpsContextHints"] = BuildRevitOpsContextHints(view);

            return JsonConvert.SerializeObject(payload, Formatting.Indented);
        }

        /// <summary>
        /// Per-slab suggested <c>wallIds</c> for <c>analyze_*</c> / <c>repair_*</c> when a level has many walls
        /// (avoids bad chains from interior partitions). Geometry comes from the model DB, not the active view.
        /// </summary>
        private static Dictionary<string, object?> BuildFootprintRepairHints(Document doc)
        {
            var pad = UnitUtils.ConvertToInternalUnits(FootprintHintBboxPadMeters, UnitTypeId.Meters);
            var floors = new List<Dictionary<string, object?>>();
            foreach (var floor in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
            {
                if (floors.Count >= MaxFootprintHintFloors)
                {
                    break;
                }

                var hint = BuildOneSlabWallHint(doc, floor.LevelId, floor.get_BoundingBox(null), pad);
                if (hint == null)
                {
                    continue;
                }

                hint["floorId"] = floor.Id.IntegerValue;
                floors.Add(hint);
            }

            var ceilings = new List<Dictionary<string, object?>>();
            foreach (var ceiling in new FilteredElementCollector(doc).OfClass(typeof(Ceiling)).Cast<Ceiling>())
            {
                if (ceilings.Count >= MaxFootprintHintCeilings)
                {
                    break;
                }

                var hint = BuildOneSlabWallHint(doc, ceiling.LevelId, ceiling.get_BoundingBox(null), pad);
                if (hint == null)
                {
                    continue;
                }

                hint["ceilingId"] = ceiling.Id.IntegerValue;
                ceilings.Add(hint);
            }

            return new Dictionary<string, object?>
            {
                ["note"] =
                    "When a Room exists for the slab zone, analyze compares to the Room boundary first (optional roomId + boundaryLocation). Use suggestedWallIds for wall-chain fallback and for repair_floor_to_wall_footprint / repair_ceiling_to_wall_footprint on busy levels. IDs are heuristic (bbox overlap); verify with selection if needed.",
                ["floors"] = floors,
                ["ceilings"] = ceilings
            };
        }

        private static Dictionary<string, object?>? BuildOneSlabWallHint(
            Document doc,
            ElementId levelId,
            BoundingBoxXYZ? bb,
            double padInternal)
        {
            if (bb == null || levelId == ElementId.InvalidElementId)
            {
                return null;
            }

            var lvl = doc.GetElement(levelId) as Level;
            var levelName = lvl?.Name;
            var mn = new XYZ(bb.Min.X - padInternal, bb.Min.Y - padInternal, bb.Min.Z - padInternal);
            var mx = new XYZ(bb.Max.X + padInternal, bb.Max.Y + padInternal, bb.Max.Z + padInternal);
            Outline outline;
            try
            {
                outline = new Outline(mn, mx);
            }
            catch
            {
                return null;
            }

            ICollection<ElementId> wallIdColl;
            try
            {
                var bf = new BoundingBoxIntersectsFilter(outline);
                wallIdColl = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .WherePasses(bf)
                    .ToElementIds();
            }
            catch
            {
                return null;
            }

            var suggested = new List<long>();
            foreach (var wid in wallIdColl)
            {
                if (doc.GetElement(wid) is not Wall w || w.LevelId != levelId)
                {
                    continue;
                }

                suggested.Add(w.Id.IntegerValue);
                if (suggested.Count >= MaxSuggestedWallIdsPerSlab)
                {
                    break;
                }
            }

            return new Dictionary<string, object?>
            {
                ["levelName"] = levelName,
                ["suggestedWallIds"] = suggested
            };
        }

        private static Dictionary<string, string> BuildRevitOpsContextHints(View? view)
        {
            var hints = new Dictionary<string, string>
            {
                ["repair_floor_to_wall_footprint"] =
                    "Uses wall location curves from the model (not the active view). If the level has many walls, pass wallIds — use footprintRepairHints.floors[].suggestedWallIds for the matching floorId when available.",
                ["repair_floor_to_room_footprint"] =
                    "Rebuilds the floor from Room.GetBoundarySegments (same as create_floor_from_room). Requires floorId + roomId on the same level; optional boundaryLocation.",
                ["analyze_floor_wall_footprint"] =
                    "Room-first when a Room is associated: optional roomId + boundaryLocation (match create_floor_from_room). Otherwise wall-chain metrics. Optional wallIds from footprintRepairHints on busy levels. 3D view is fine.",
                ["repair_ceiling_to_wall_footprint"] =
                    "Same pattern as floor repair; use footprintRepairHints.ceilings[].suggestedWallIds when helpful.",
                ["repair_ceiling_to_room_footprint"] =
                    "Rebuilds the ceiling from the room boundary (same as create_ceiling_from_room). ceilingId + roomId; optional boundaryLocation.",
                ["analyze_ceiling_wall_footprint"] =
                    "Same Room-first pattern as floor analyze; optional roomId, boundaryLocation, wallIds; 3D view is fine.",
                ["create_ceiling_from_room"] =
                    "After boundary: sets Height Offset From Level using walls near the room + inserts. Default ceilingKind false_ceiling (teto falso) with queda from falseCeilingDropMeters or plugin DefaultFalseCeilingDropMeters (~0.30 m). Use ceilingKind slab_painted (laje pintada) for flush to wall top. Never places the ceiling through openings — clamped above door/window/opening bbox.",
                ["create_ceiling"] =
                    "Same vertical rules as create_ceiling_from_room; optional ceilingKind, falseCeilingDropMeters.",
                ["change_ceiling_kind"] =
                    "Recompute ceiling height: ceilingId (or elementId), ceilingKind false_ceiling|slab_painted (aliases: teto_falso, laje), optional falseCeilingDropMeters.",
                ["create_pillar"] =
                    "Column (structural or architectural): pillarTypeName or columnTypeName must match namedTypesForRevitOps.structuralColumnTypeNames or architecturalColumnTypeNames (\"Family : Type\"). Plan location in metres (model XY). Optional levelName (base), topLevelName, heightMeters (unconnected top from base level), topOffsetMeters, baseOffsetMeters, rotationDegrees, name (Comments)."
            };

            if (view != null && view is not ViewPlan)
            {
                hints["activeView"] =
                    "Active view is not a floor plan — planGeometryInActiveView may be omitted. Slab footprint repair/analyze still work; prefer footprintRepairHints + element ids from selection.";
            }

            return hints;
        }

        /// <summary>Type names the LLM should use in revitOps (walls, floors, ceilings, doors, windows, structural/architectural columns, generic families).</summary>
        private static Dictionary<string, object?> BuildNamedTypesForRevitOps(Document doc)
        {
            const int cap = 36;
            string[] TakeDistinct(IEnumerable<string> seq) =>
                seq.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(cap).ToArray();

            var wallTypes = TakeDistinct(
                new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Select(t => t.Name));

            var floorTypes = TakeDistinct(
                new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Select(t => t.Name));

            var ceilingTypes = TakeDistinct(
                new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<CeilingType>().Select(t => t.Name));

            var doorLabels = new List<string>();
            foreach (var s in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Doors)
                         .OfClass(typeof(FamilySymbol))
                         .Cast<FamilySymbol>())
            {
                if (doorLabels.Count >= cap)
                {
                    break;
                }

                doorLabels.Add((s.Family?.Name ?? "?") + " : " + s.Name);
            }

            var windowLabels = new List<string>();
            foreach (var s in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Windows)
                         .OfClass(typeof(FamilySymbol))
                         .Cast<FamilySymbol>())
            {
                if (windowLabels.Count >= cap)
                {
                    break;
                }

                windowLabels.Add((s.Family?.Name ?? "?") + " : " + s.Name);
            }

            var structuralColumnLabels = new List<string>();
            var structuralColumnSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_StructuralColumns)
                         .OfClass(typeof(FamilySymbol))
                         .Cast<FamilySymbol>())
            {
                if (structuralColumnLabels.Count >= cap)
                {
                    break;
                }

                var label = (s.Family?.Name ?? "?") + " : " + s.Name;
                if (structuralColumnSeen.Add(label))
                {
                    structuralColumnLabels.Add(label);
                }
            }

            var architecturalColumnLabels = new List<string>();
            var architecturalColumnSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Columns)
                         .OfClass(typeof(FamilySymbol))
                         .Cast<FamilySymbol>())
            {
                if (architecturalColumnLabels.Count >= cap)
                {
                    break;
                }

                var label = (s.Family?.Name ?? "?") + " : " + s.Name;
                if (architecturalColumnSeen.Add(label))
                {
                    architecturalColumnLabels.Add(label);
                }
            }

            var sampleFamilies = new List<string>();
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
            {
                if (sampleFamilies.Count >= cap)
                {
                    break;
                }

                if (s.Category?.CategoryType != CategoryType.Model)
                {
                    continue;
                }

                sampleFamilies.Add((s.Family?.Name ?? "?") + " : " + s.Name);
            }

            return new Dictionary<string, object?>
            {
                ["wallTypeNames"] = wallTypes,
                ["floorTypeNames"] = floorTypes,
                ["ceilingTypeNames"] = ceilingTypes,
                ["doorTypeNames"] = doorLabels.ToArray(),
                ["windowTypeNames"] = windowLabels.ToArray(),
                ["structuralColumnTypeNames"] = structuralColumnLabels.ToArray(),
                ["architecturalColumnTypeNames"] = architecturalColumnLabels.ToArray(),
                ["sampleLoadableFamilyTypes"] = sampleFamilies.ToArray(),
                ["note"] =
                    "Use these strings for wallTypeName, floorTypeName, ceilingTypeName, doorTypeName, windowTypeName, structuralColumnTypeNames or architecturalColumnTypeNames (create_pillar pillarTypeName/columnTypeName), familyTypeName in revitOps."
            };
        }

        /// <summary>
        /// Wall/door/window/room endpoints in model XY as metres (same convention as sketch create_wall), scoped to elements visible in the active view when it is a plan view.
        /// </summary>
        private static object? TryBuildPlanGeometryInActiveView(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;
            if (view == null)
            {
                return new Dictionary<string, object?>
                {
                    ["omitted"] = true,
                    ["reason"] = "No active view."
                };
            }

            if (view is not ViewPlan)
            {
                return new Dictionary<string, object?>
                {
                    ["omitted"] = true,
                    ["reason"] = "Active view is not a plan (e.g. floor plan). Open a plan view for XY geometry; counts above still apply."
                };
            }

            try
            {
                var collector = new FilteredElementCollector(doc, view.Id);
                var wallsOut = new List<Dictionary<string, object?>>();
                var doorsOut = new List<Dictionary<string, object?>>();
                var windowsOut = new List<Dictionary<string, object?>>();
                var roomsOut = new List<Dictionary<string, object?>>();
                double? minX = null, minY = null, maxX = null, maxY = null;

                void ExpandFootprint(double x, double y)
                {
                    minX = minX.HasValue ? Math.Min(minX.Value, x) : x;
                    minY = minY.HasValue ? Math.Min(minY.Value, y) : y;
                    maxX = maxX.HasValue ? Math.Max(maxX.Value, x) : x;
                    maxY = maxY.HasValue ? Math.Max(maxY.Value, y) : y;
                }

                foreach (var el in collector)
                {
                    if (el is Wall wall && wallsOut.Count < MaxGeometryWalls)
                    {
                        var row = SerializeWallGeometry(doc, wall, ExpandFootprint);
                        if (row != null)
                        {
                            wallsOut.Add(row);
                        }
                    }
                }

                foreach (var el in collector)
                {
                    if (el is FamilyInstance fi)
                    {
                        if (fi.Category?.BuiltInCategory == BuiltInCategory.OST_Doors && doorsOut.Count < MaxGeometryDoors)
                        {
                            var row = SerializeDoorGeometry(doc, fi, ExpandFootprint);
                            if (row != null)
                            {
                                doorsOut.Add(row);
                            }
                        }
                        else if (fi.Category?.BuiltInCategory == BuiltInCategory.OST_Windows &&
                                 windowsOut.Count < MaxGeometryWindows)
                        {
                            var row = SerializeWindowGeometry(doc, fi, ExpandFootprint);
                            if (row != null)
                            {
                                windowsOut.Add(row);
                            }
                        }
                    }
                }

                foreach (var el in collector)
                {
                    if (el is SpatialElement sp &&
                        sp.Category?.BuiltInCategory == BuiltInCategory.OST_Rooms &&
                        roomsOut.Count < MaxGeometryRooms)
                    {
                        var row = SerializeRoomGeometry(doc, sp, ExpandFootprint);
                        if (row != null)
                        {
                            roomsOut.Add(row);
                        }
                    }
                }

                Dictionary<string, object?>? footprint = null;
                if (minX.HasValue && minY.HasValue && maxX.HasValue && maxY.HasValue)
                {
                    footprint = new Dictionary<string, object?>
                    {
                        ["minX"] = RoundM(minX.Value),
                        ["minY"] = RoundM(minY.Value),
                        ["maxX"] = RoundM(maxX.Value),
                        ["maxY"] = RoundM(maxY.Value)
                    };
                }

                var totalWalls = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).GetElementCount();
                var totalDoors = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Doors).GetElementCount();
                var totalWindows = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Windows).GetElementCount();
                var totalRooms = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Rooms).GetElementCount();

                return new Dictionary<string, object?>
                {
                    ["viewName"] = view.Name,
                    ["coordinateSystem"] =
                        "Model XY in metres (internal Revit feet converted). Same XY convention as create_wall / sketch upload.",
                    ["walls"] = wallsOut,
                    ["doors"] = doorsOut,
                    ["windows"] = windowsOut,
                    ["rooms"] = roomsOut,
                    ["footprintMeters"] = footprint,
                    ["truncation"] = new Dictionary<string, object?>
                    {
                        ["wallsReturned"] = wallsOut.Count,
                        ["wallsInView"] = totalWalls,
                        ["doorsReturned"] = doorsOut.Count,
                        ["doorsInView"] = totalDoors,
                        ["windowsReturned"] = windowsOut.Count,
                        ["windowsInView"] = totalWindows,
                        ["roomsReturned"] = roomsOut.Count,
                        ["roomsInView"] = totalRooms
                    }
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?>
                {
                    ["omitted"] = true,
                    ["reason"] = "Geometry export failed: " + ex.Message
                };
            }
        }

        private static Dictionary<string, object?>? SerializeWallGeometry(
            Document doc,
            Wall wall,
            Action<double, double> expandFootprint)
        {
            if (wall.Location is not LocationCurve lc || lc.Curve is not Line line)
            {
                return null;
            }

            var s = line.GetEndPoint(0);
            var e = line.GetEndPoint(1);
            var sx = InternalToMeters(s.X);
            var sy = InternalToMeters(s.Y);
            var ex = InternalToMeters(e.X);
            var ey = InternalToMeters(e.Y);
            expandFootprint(sx, sy);
            expandFootprint(ex, ey);
            var lvl = wall.LevelId != ElementId.InvalidElementId ? doc.GetElement(wall.LevelId) as Level : null;
            return new Dictionary<string, object?>
            {
                ["elementId"] = wall.Id.IntegerValue,
                ["startX"] = RoundM(sx),
                ["startY"] = RoundM(sy),
                ["endX"] = RoundM(ex),
                ["endY"] = RoundM(ey),
                ["lengthMeters"] = RoundM(InternalToMeters(line.Length)),
                ["levelName"] = lvl?.Name
            };
        }

        private static Dictionary<string, object?>? SerializeDoorGeometry(
            Document doc,
            FamilyInstance fi,
            Action<double, double> expandFootprint)
        {
            if (fi.Location is not LocationPoint lp)
            {
                return null;
            }

            var p = lp.Point;
            var x = InternalToMeters(p.X);
            var y = InternalToMeters(p.Y);
            expandFootprint(x, y);
            var lvl = fi.LevelId != ElementId.InvalidElementId ? doc.GetElement(fi.LevelId) as Level : null;
            var hostWall = fi.Host as Wall;
            return new Dictionary<string, object?>
            {
                ["elementId"] = fi.Id.IntegerValue,
                ["locationX"] = RoundM(x),
                ["locationY"] = RoundM(y),
                ["hostWallId"] = hostWall != null ? hostWall.Id.IntegerValue : null,
                ["levelName"] = lvl?.Name,
                ["symbolName"] = fi.Symbol?.Name
            };
        }

        private static Dictionary<string, object?>? SerializeWindowGeometry(
            Document doc,
            FamilyInstance fi,
            Action<double, double> expandFootprint)
        {
            if (fi.Location is not LocationPoint lp)
            {
                return null;
            }

            var p = lp.Point;
            var x = InternalToMeters(p.X);
            var y = InternalToMeters(p.Y);
            expandFootprint(x, y);
            var lvl = fi.LevelId != ElementId.InvalidElementId ? doc.GetElement(fi.LevelId) as Level : null;
            var hostWall = fi.Host as Wall;
            return new Dictionary<string, object?>
            {
                ["elementId"] = fi.Id.IntegerValue,
                ["locationX"] = RoundM(x),
                ["locationY"] = RoundM(y),
                ["hostWallId"] = hostWall != null ? hostWall.Id.IntegerValue : null,
                ["levelName"] = lvl?.Name,
                ["symbolName"] = fi.Symbol?.Name
            };
        }

        private static Dictionary<string, object?>? SerializeRoomGeometry(
            Document doc,
            SpatialElement room,
            Action<double, double> expandFootprint)
        {
            if (room.Location is not LocationPoint lp)
            {
                return null;
            }

            var p = lp.Point;
            var x = InternalToMeters(p.X);
            var y = InternalToMeters(p.Y);
            expandFootprint(x, y);
            var lvl = room.LevelId != ElementId.InvalidElementId ? doc.GetElement(room.LevelId) as Level : null;
            double areaM;
            try
            {
                areaM = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters);
            }
            catch
            {
                areaM = 0;
            }

            return new Dictionary<string, object?>
            {
                ["elementId"] = room.Id.IntegerValue,
                ["centerX"] = RoundM(x),
                ["centerY"] = RoundM(y),
                ["name"] = string.IsNullOrWhiteSpace(room.Name) ? "Room" : room.Name,
                ["areaSquareMeters"] = RoundM(areaM),
                ["levelName"] = lvl?.Name
            };
        }

        private static double InternalToMeters(double internalFeet)
        {
            return UnitUtils.ConvertFromInternalUnits(internalFeet, UnitTypeId.Meters);
        }

        private static double RoundM(double v) => Math.Round(v, 3);

        /// <summary>Detailed snapshot of current selection (capped).</summary>
        public static string BuildSelectionSnapshot(UIDocument uidoc, int maxElements = 24, int maxParamsPerElement = 120)
        {
            var doc = uidoc.Document;
            var ids = uidoc.Selection.GetElementIds();
            var total = ids.Count;
            var elements = new List<Dictionary<string, object?>>();
            var n = 0;
            foreach (var id in ids)
            {
                if (n >= maxElements)
                {
                    break;
                }

                var el = doc.GetElement(id);
                if (el == null)
                {
                    continue;
                }

                elements.Add(SerializeElement(doc, el, maxParamsPerElement));
                n++;
            }

            var floorIds = new List<long>();
            var ceilingIds = new List<long>();
            var roomIds = new List<long>();
            foreach (var id in ids)
            {
                var el = doc.GetElement(id);
                if (el is Floor f)
                {
                    floorIds.Add(f.Id.IntegerValue);
                }
                else if (el is Ceiling c)
                {
                    ceilingIds.Add(c.Id.IntegerValue);
                }
                else if (el is Room r)
                {
                    roomIds.Add(r.Id.IntegerValue);
                }
            }

            var payload = new Dictionary<string, object?>
            {
                ["totalSelected"] = total,
                ["elementsReturned"] = elements.Count,
                ["elements"] = elements
            };

            if (floorIds.Count > 0)
            {
                payload["floorIdsInSelection"] = floorIds;
            }

            if (ceilingIds.Count > 0)
            {
                payload["ceilingIdsInSelection"] = ceilingIds;
            }

            if (roomIds.Count > 0)
            {
                payload["roomIdsInSelection"] = roomIds;
            }

            if (total > elements.Count)
            {
                payload["truncated"] = true;
                payload["note"] = "Lista truncada; pede ao utilizador para reduzir a seleção ou filtrar por categoria se precisares de todos.";
            }

            return JsonConvert.SerializeObject(payload, Formatting.Indented);
        }

        private static Dictionary<string, object?> SerializeElement(Document doc, Element el, int maxParams)
        {
            var typeId = el.GetTypeId();
            var typeEl = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) as ElementType : null;

            var row = new Dictionary<string, object?>
            {
                ["id"] = el.Id.IntegerValue,
                ["uniqueId"] = el.UniqueId,
                ["category"] = el.Category?.Name,
                ["name"] = string.IsNullOrEmpty(el.Name) ? null : el.Name,
                ["typeName"] = typeEl?.Name
            };

            if (el is FamilyInstance fi)
            {
                if (fi.Host != null)
                {
                    row["hostId"] = fi.Host.Id.IntegerValue;
                    row["hostCategory"] = fi.Host.Category?.Name;
                }

                if (fi.LevelId != null && fi.LevelId != ElementId.InvalidElementId)
                {
                    var lvl = doc.GetElement(fi.LevelId) as Level;
                    row["level"] = lvl?.Name;
                }
            }
            else if (el is Wall w)
            {
                if (w.LevelId != ElementId.InvalidElementId)
                {
                    var lvl = doc.GetElement(w.LevelId) as Level;
                    row["level"] = lvl?.Name;
                }

                if (w.Location is LocationCurve lc)
                {
                    row["length"] = FormatLength(doc, lc.Curve.Length);
                }
            }
            else if (el is Room rm)
            {
                if (rm.LevelId != ElementId.InvalidElementId)
                {
                    var lvl = doc.GetElement(rm.LevelId) as Level;
                    row["level"] = lvl?.Name;
                }

                try
                {
                    row["number"] = rm.Number;
                }
                catch
                {
                    row["number"] = rm.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                }

                try
                {
                    row["areaSquareMeters"] = RoundM(UnitUtils.ConvertFromInternalUnits(rm.Area, UnitTypeId.SquareMeters));
                }
                catch
                {
                    // unplaced / no area
                }

                row["slabFromRoomRevitOp"] = "create_floor_from_room";
                row["slabFromRoomPayload"] = new Dictionary<string, object?>
                {
                    ["roomId"] = rm.Id.IntegerValue,
                    ["boundaryLocation"] = "finish"
                };
                row["ceilingFromRoomRevitOp"] = "create_ceiling_from_room";
                row["ceilingFromRoomPayload"] = new Dictionary<string, object?>
                {
                    ["roomId"] = rm.Id.IntegerValue,
                    ["boundaryLocation"] = "finish",
                    ["ceilingKind"] = "false_ceiling"
                };
            }

            row["parameters"] = CollectParameters(doc, el, typeEl, maxParams);
            return row;
        }

        /// <summary>
        /// Collects instance + type parameters, including ElementId (levels, work planes), empty strings when writable,
        /// and built-in ids for stable <c>set_parameter</c> calls.
        /// </summary>
        private static List<Dictionary<string, object?>> CollectParameters(
            Document doc,
            Element instance,
            ElementType? type,
            int maxParams)
        {
            var list = new List<Dictionary<string, object?>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddFrom(Element source, string scope)
            {
                foreach (Parameter p in source.Parameters)
                {
                    if (list.Count >= maxParams)
                    {
                        return;
                    }

                    if (p == null)
                    {
                        continue;
                    }

                    var def = p.Definition;
                    if (def == null)
                    {
                        continue;
                    }

                    var name = def.Name;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var dedupeKey = scope + "|" + name;
                    if (!seen.Add(dedupeKey))
                    {
                        continue;
                    }

                    if (p.StorageType == StorageType.None)
                    {
                        continue;
                    }

                    if (!p.HasValue)
                    {
                        if (p.StorageType == StorageType.String)
                        {
                            // include empty user-editable strings (e.g. Comments)
                        }
                        else if (p.StorageType == StorageType.ElementId)
                        {
                            continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    var formatted = FormatParameterValue(doc, p);
                    if (formatted == null && p.StorageType != StorageType.String)
                    {
                        continue;
                    }

                    var row = new Dictionary<string, object?>
                    {
                        ["scope"] = scope,
                        ["name"] = name,
                        ["value"] = formatted ?? string.Empty,
                        ["readOnly"] = p.IsReadOnly,
                        ["storage"] = p.StorageType.ToString()
                    };

                    if (def is InternalDefinition idef && idef.BuiltInParameter != BuiltInParameter.INVALID)
                    {
                        row["builtInParameter"] = idef.BuiltInParameter.ToString();
                    }

                    list.Add(row);
                }
            }

            AddFrom(instance, "instance");
            if (type != null)
            {
                AddFrom(type, "type");
            }

            return list;
        }

        private static string? FormatParameterValue(Document doc, Parameter p)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.ElementId:
                    {
                        var id = p.AsElementId();
                        if (id == ElementId.InvalidElementId)
                        {
                            return null;
                        }

                        var refEl = doc.GetElement(id);
                        if (refEl != null)
                        {
                            var nm = refEl.Name;
                            return string.IsNullOrWhiteSpace(nm)
                                ? refEl.Id.IntegerValue.ToString(CultureInfo.InvariantCulture)
                                : nm + " (id:" + refEl.Id.IntegerValue + ")";
                        }

                        return id.IntegerValue.ToString(CultureInfo.InvariantCulture);
                    }
                    case StorageType.String:
                        return p.AsString() ?? string.Empty;
                    case StorageType.Double:
                        return p.AsValueString() ?? p.AsDouble().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Integer:
                        return p.AsValueString() ?? p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    default:
                        return p.AsValueString();
                }
            }
            catch
            {
                return p.AsValueString();
            }
        }

        private static int CountCategory(Document doc, BuiltInCategory bic)
        {
            try
            {
                return new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .Count;
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatLength(Document doc, double internalFeet)
        {
            try
            {
                return UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, internalFeet, false);
            }
            catch
            {
                var m = UnitUtils.ConvertFromInternalUnits(internalFeet, UnitTypeId.Meters);
                return Math.Round(m, 3).ToString(CultureInfo.InvariantCulture) + " m (approx)";
            }
        }
    }
}
