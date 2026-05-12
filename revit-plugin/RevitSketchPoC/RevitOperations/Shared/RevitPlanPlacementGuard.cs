using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitSketchPoC.RevitOperations.Shared
{
    /// <summary>Batch kind for plan placement spacing (doors/windows vs columns for create_pillar).</summary>
    public enum PlanPlacementBatchKind
    {
        Standard,
        Pillar
    }

    /// <summary>Reduces overlapping LLM placements for doors, windows, point-hosted families, and pillars on the same level.</summary>
    public static class RevitPlanPlacementGuard
    {
        /// <summary>Minimum plan distance (m) from existing doors/windows in the document.</summary>
        public const double MinSeparationFromOpeningsMeters = 0.65;

        /// <summary>Minimum plan distance (m) between two placements in the same revitOps batch (same level), standard points.</summary>
        public const double MinBatchSeparationMeters = 0.22;

        /// <summary>Minimum plan distance (m) from existing or batched columns (structural or architectural, create_pillar).</summary>
        public const double MinSeparationFromColumnsMeters = 0.40;

        /// <summary>Minimum plan distance (m) from wall location curves (XY) for pillar placement — avoids core overlap.</summary>
        public const double MinSeparationFromWallsMeters = 0.15;

        /// <summary>Validates then records the point (doors/windows/family instances).</summary>
        public static void AssertNewPlanPoint(
            Document doc,
            Level level,
            double xMeters,
            double yMeters,
            List<(double x, double y, ElementId levelId, PlanPlacementBatchKind kind)> batch,
            bool checkExistingDoorWindow = true,
            PlanPlacementBatchKind newPointKind = PlanPlacementBatchKind.Standard)
        {
            ValidateNewPlanPoint(doc, level, xMeters, yMeters, batch, checkExistingDoorWindow, newPointKind);
            batch.Add((xMeters, yMeters, level.Id, newPointKind));
        }

        /// <summary>Validates plan placement without mutating the batch (use <see cref="AddPlanPointToBatch"/> after successful create).</summary>
        public static void ValidateNewPlanPoint(
            Document doc,
            Level level,
            double xMeters,
            double yMeters,
            IReadOnlyList<(double x, double y, ElementId levelId, PlanPlacementBatchKind kind)> batch,
            bool checkExistingDoorWindow = true,
            PlanPlacementBatchKind newPointKind = PlanPlacementBatchKind.Standard)
        {
            foreach (var p in batch)
            {
                if (!p.levelId.Equals(level.Id))
                {
                    continue;
                }

                var d = PlanDistanceMeters(p.x, p.y, xMeters, yMeters);
                var minRequired = MinBatchSeparationForPair(p.kind, newPointKind);
                if (d < minRequired)
                {
                    throw new InvalidOperationException(
                        "Placement too close to another point in the same request (minimum ~" +
                        minRequired.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                        " m for this placement type). Offset coordinates or use hostWallId.");
                }
            }

            var testXf = RevitInternalUnits.MetersToFeet(xMeters);
            var testYf = RevitInternalUnits.MetersToFeet(yMeters);
            var testZ = level.Elevation;
            var testPoint = new XYZ(testXf, testYf, testZ);

            if (checkExistingDoorWindow)
            {
                foreach (var el in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
                {
                    if (el is not FamilyInstance fi)
                    {
                        continue;
                    }

                    if (!fi.LevelId.Equals(level.Id))
                    {
                        continue;
                    }

                    var bic = fi.Category?.BuiltInCategory;
                    if (bic != BuiltInCategory.OST_Doors && bic != BuiltInCategory.OST_Windows)
                    {
                        continue;
                    }

                    if (fi.Location is not LocationPoint lp)
                    {
                        continue;
                    }

                    var q = lp.Point;
                    var dFt = Math.Sqrt((q.X - testXf) * (q.X - testXf) + (q.Y - testYf) * (q.Y - testYf));
                    var dM = dFt * 0.3048;
                    if (dM < MinSeparationFromOpeningsMeters)
                    {
                        throw new InvalidOperationException(
                            "Point is within " + MinSeparationFromOpeningsMeters +
                            " m of an existing door/window on this level; choose another location or hostWallId.");
                    }
                }
            }

            if (newPointKind == PlanPlacementBatchKind.Pillar)
            {
                foreach (var columnCategory in new[]
                         {
                             BuiltInCategory.OST_StructuralColumns,
                             BuiltInCategory.OST_Columns
                         })
                {
                    foreach (var el in new FilteredElementCollector(doc)
                                 .OfCategory(columnCategory)
                                 .WhereElementIsNotElementType())
                    {
                        if (el is not FamilyInstance fi)
                        {
                            continue;
                        }

                        if (!fi.LevelId.Equals(level.Id))
                        {
                            continue;
                        }

                        if (fi.Location is not LocationPoint lp)
                        {
                            continue;
                        }

                        var q = lp.Point;
                        var dFt = Math.Sqrt((q.X - testXf) * (q.X - testXf) + (q.Y - testYf) * (q.Y - testYf));
                        var dM = dFt * 0.3048;
                        if (dM < MinSeparationFromColumnsMeters)
                        {
                            throw new InvalidOperationException(
                                "create_pillar: point is within " + MinSeparationFromColumnsMeters +
                                " m of an existing column on this level; offset the location.");
                        }
                    }
                }

                foreach (var wall in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    if (wall.LevelId != level.Id)
                    {
                        continue;
                    }

                    if (wall.Location is not LocationCurve lc || lc.Curve is not Line line)
                    {
                        continue;
                    }

                    var proj = line.Project(testPoint);
                    if (proj == null)
                    {
                        continue;
                    }

                    var dM = proj.Distance * 0.3048;
                    if (dM < MinSeparationFromWallsMeters)
                    {
                        throw new InvalidOperationException(
                            "create_pillar: point is within " + MinSeparationFromWallsMeters +
                            " m of a wall centerline on this level (risk of clashing with wall core); offset away from the wall.");
                    }
                }
            }
        }

        public static void AddPlanPointToBatch(
            List<(double x, double y, ElementId levelId, PlanPlacementBatchKind kind)> batch,
            double xMeters,
            double yMeters,
            Level level,
            PlanPlacementBatchKind kind = PlanPlacementBatchKind.Standard)
        {
            batch.Add((xMeters, yMeters, level.Id, kind));
        }

        private static double MinBatchSeparationForPair(PlanPlacementBatchKind a, PlanPlacementBatchKind b)
        {
            if (a == PlanPlacementBatchKind.Pillar && b == PlanPlacementBatchKind.Pillar)
            {
                return MinSeparationFromColumnsMeters;
            }

            if (a == PlanPlacementBatchKind.Pillar || b == PlanPlacementBatchKind.Pillar)
            {
                return MinSeparationFromOpeningsMeters;
            }

            return MinBatchSeparationMeters;
        }

        private static double PlanDistanceMeters(double x1, double y1, double x2, double y2)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
