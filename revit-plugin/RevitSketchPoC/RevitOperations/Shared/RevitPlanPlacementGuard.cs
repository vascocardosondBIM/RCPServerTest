using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace RevitSketchPoC.RevitOperations.Shared
{
    /// <summary>Reduces overlapping LLM placements for doors, windows, and point-hosted families on the same level.</summary>
    public static class RevitPlanPlacementGuard
    {
        /// <summary>Minimum plan distance (m) from existing doors/windows in the document.</summary>
        public const double MinSeparationFromOpeningsMeters = 0.65;

        /// <summary>Minimum plan distance (m) between two placements in the same revitOps batch (same level).</summary>
        public const double MinBatchSeparationMeters = 0.22;

        public static void AssertNewPlanPoint(
            Document doc,
            Level level,
            double xMeters,
            double yMeters,
            List<(double x, double y, ElementId levelId)> batch,
            bool checkExistingDoorWindow = true)
        {
            foreach (var p in batch)
            {
                if (!p.levelId.Equals(level.Id))
                {
                    continue;
                }

                if (PlanDistanceMeters(p.x, p.y, xMeters, yMeters) < MinBatchSeparationMeters)
                {
                    throw new InvalidOperationException(
                        "Placement too close to another point in the same request (batch duplicate ~" +
                        MinBatchSeparationMeters + " m). Offset coordinates or use hostWallId.");
                }
            }

            if (checkExistingDoorWindow)
            {
                var testXf = RevitInternalUnits.MetersToFeet(xMeters);
                var testYf = RevitInternalUnits.MetersToFeet(yMeters);
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

            batch.Add((xMeters, yMeters, level.Id));
        }

        private static double PlanDistanceMeters(double x1, double y1, double x2, double y2)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
