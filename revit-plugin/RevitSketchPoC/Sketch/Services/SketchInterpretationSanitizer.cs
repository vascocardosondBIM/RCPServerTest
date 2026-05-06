using RevitSketchPoC.Sketch.Contracts;
using RevitSketchPoC.Core.Geometry;
using System;
using System.Collections.Generic;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Snaps coordinates to a small grid, removes degenerate wall segments (LLM noise), then removes shorts again after snap.
    /// </summary>
    public static class SketchInterpretationSanitizer
    {
        public static void SanitizeInPlace(SketchInterpretation interpretation)
        {
            if (interpretation == null)
            {
                return;
            }

            interpretation.Walls ??= new List<WallSegment>();
            interpretation.Rooms ??= new List<RoomRegion>();
            interpretation.Doors ??= new List<DoorPlacement>();
            interpretation.Windows ??= new List<WindowPlacement>();
            interpretation.Floors ??= new List<FloorBoundary>();

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

            if (interpretation.Windows != null)
            {
                foreach (var w in interpretation.Windows)
                {
                    if (w.Location != null)
                    {
                        SnapPoint(w.Location);
                    }
                }
            }

            if (interpretation.Floors != null)
            {
                foreach (var f in interpretation.Floors)
                {
                    if (f.Boundary == null)
                    {
                        continue;
                    }

                    foreach (var p in f.Boundary)
                    {
                        SnapPoint(p);
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
            return PlanGeometryRules.Snap(v);
        }

        private static void RemoveShortWalls(SketchInterpretation interpretation)
        {
            if (interpretation.Walls == null)
            {
                return;
            }

            interpretation.Walls.RemoveAll(w => SegmentLengthMeters(w) < PlanGeometryRules.MinWallSegmentLengthMeters);
        }

        private static double SegmentLengthMeters(WallSegment w)
        {
            var dx = w.End.X - w.Start.X;
            var dy = w.End.Y - w.Start.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
