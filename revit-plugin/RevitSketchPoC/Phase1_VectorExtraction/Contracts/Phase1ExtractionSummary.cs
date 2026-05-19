using System.Collections.Generic;

namespace RevitSketchPoC.Phase1_VectorExtraction.Contracts
{
    public sealed class Phase1ElementCounts
    {
        public int Lines { get; set; }
        public int Polylines { get; set; }
        public int Beziers { get; set; }
        public int Rectangles { get; set; }
        public int Hatches { get; set; }
        public int Words { get; set; }
        public int TextBlocks { get; set; }
        public int TextSpans { get; set; }
        public int Intersections { get; set; }
        public int Adjacency { get; set; }
        public int CleanLines { get; set; }
        public int CleanRectangles { get; set; }

        public int GeometryTotal => Lines + Polylines + Beziers + Rectangles + Hatches;
        public int TextTotal => Words + TextBlocks + TextSpans;
        public int GrandTotal => GeometryTotal + TextTotal + Intersections + Adjacency;
    }

    public sealed class Phase1RegionSummary
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public Phase1ElementCounts Counts { get; set; } = new Phase1ElementCounts();
    }

    public sealed class Phase1ExtractionSummary
    {
        public string ScopeLabel { get; set; } = "PDF completo";
        public Phase1ElementCounts FullPage { get; set; } = new Phase1ElementCounts();
        public List<Phase1RegionSummary> Regions { get; set; } = new List<Phase1RegionSummary>();
    }
}
