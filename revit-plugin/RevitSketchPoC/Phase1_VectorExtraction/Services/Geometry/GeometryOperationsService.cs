using System;
using System.Collections.Generic;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services.Geometry
{
    /// <summary>
    /// Operações geométricas leves (equivalente funcional a Shapely) para topologia e GNN.
    /// </summary>
    public static class GeometryOperationsService
    {
        public static bool BboxIntersects(IReadOnlyList<double> a, IReadOnlyList<double> b)
        {
            if (a.Count < 4 || b.Count < 4)
            {
                return false;
            }

            return a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1];
        }

        public static bool BboxContains(IReadOnlyList<double> outer, IReadOnlyList<double> inner)
        {
            if (outer.Count < 4 || inner.Count < 4)
            {
                return false;
            }

            return outer[0] <= inner[0] && outer[1] <= inner[1] &&
                   outer[2] >= inner[2] && outer[3] >= inner[3];
        }

        public static double Distance(double x1, double y1, double x2, double y2)
        {
            return Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        }

        public static bool PointsNear(double x1, double y1, double x2, double y2, double tolerance)
        {
            return Distance(x1, y1, x2, y2) <= tolerance;
        }

        public static IReadOnlyList<double> ExpandBbox(IReadOnlyList<double> bbox, double bufferPt)
        {
            return new[]
            {
                bbox[0] - bufferPt,
                bbox[1] - bufferPt,
                bbox[2] + bufferPt,
                bbox[3] + bufferPt
            };
        }
    }
}
