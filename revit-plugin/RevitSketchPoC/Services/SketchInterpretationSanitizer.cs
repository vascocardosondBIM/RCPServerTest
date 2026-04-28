using RevitSketchPoC.Contracts;
using System;

namespace RevitSketchPoC.Services
{
    /// <summary>
    /// Removes degenerate wall segments (common LLM noise that shows as random short lines in Revit).
    /// </summary>
    public static class SketchInterpretationSanitizer
    {
        private const double MinSegmentLengthMeters = 0.35;

        public static void SanitizeInPlace(SketchInterpretation interpretation)
        {
            if (interpretation?.Walls == null)
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
