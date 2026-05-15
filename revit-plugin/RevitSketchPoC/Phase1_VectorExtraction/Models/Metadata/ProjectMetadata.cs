using System;

namespace RevitSketchPoC.Phase1_VectorExtraction.Models.Metadata
{
    public sealed class ProjectMetadata
    {
        public string File { get; set; } = string.Empty;
        public int Pages { get; set; }
        public int SelectedPage { get; set; }
        public string Units { get; set; } = "mm";
        public string WorldUnits { get; set; } = "mm";
        public string? ScaleHint { get; set; }
        public double[] BboxPt { get; set; } = System.Array.Empty<double>();
        public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("o");
        public string[] LayersDetected { get; set; } = System.Array.Empty<string>();
    }
}
