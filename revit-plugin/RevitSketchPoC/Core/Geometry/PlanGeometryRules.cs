using System;

namespace RevitSketchPoC.Core.Geometry
{
    /// <summary>Shared XY geometry tolerances (metres) used by sketch and revitOps flows.</summary>
    public static class PlanGeometryRules
    {
        public const double MinWallSegmentLengthMeters = 0.35;
        public const double SnapGridMeters = 0.05;
        public const double EndpointJoinToleranceMeters = 0.05;
        public const double CollinearToleranceMeters = 0.03;

        public static double Snap(double valueMeters)
        {
            return Math.Round(valueMeters / SnapGridMeters) * SnapGridMeters;
        }
    }
}
