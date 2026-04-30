using RevitSketchPoC.Sketch.Contracts;
using System;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Snaps coordinates to a small grid, removes degenerate wall segments (LLM noise), then removes shorts again after snap.
    /// </summary>
    public static class SketchInterpretationSanitizer
    {
        private const double MinSegmentLengthMeters = 0.35;
        private const double SnapGridMeters = 0.05;

        public static void SanitizeInPlace(SketchInterpretation interpretation)
        {
            if (interpretation == null)
            {
                return;
            }

            SnapGeometry(interpretation);
            RemoveShortWalls(interpretation);
            // Snapping can collapse a segment; run twice for stability.
            SnapGeometry(interpretation);
            RemoveShortWalls(interpretation);
        }

        private static void SnapGeometry(SketchInterpretation interpretation)
        {
            if (interpretation.Walls != null)
            {
                foreach (var w in interpretation.Walls)
                {
                    SnapPoint(w.Start);
                    SnapPoint(w.End);
                }
            }

            if (interpretation.Rooms != null)
            {
                foreach (var room in interpretation.Rooms)
                {
                    if (room.Boundary == null)
                    {
                        continue;
                    }

                    foreach (var p in room.Boundary)
                    {
                        SnapPoint(p);
                    }
                }
            }

            if (interpretation.Doors != null)
            {
                foreach (var d in interpretation.Doors)
                {
                    if (d.Location != null)
                    {
                        SnapPoint(d.Location);
                    }
                }
            }
        }

        private static void SnapPoint(Point2D p)
        {
            p.X = Snap(p.X);
            p.Y = Snap(p.Y);
        }

        private static double Snap(double v)
        {
            if (SnapGridMeters <= 0)
            {
                return v;
            }

            return Math.Round(v / SnapGridMeters) * SnapGridMeters;
        }

        private static void RemoveShortWalls(SketchInterpretation interpretation)
        {
            if (interpretation.Walls == null)
            {
                return;
            }

            interpretation.Walls.RemoveAll(w => SegmentLengthMeters(w) < MinSegmentLengthMeters);
        }

        private static double SegmentLengthMeters(WallSegment w)
        {
            var dx = w.End.X - w.Start.X;
            var dy = w.End.Y - w.Start.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
