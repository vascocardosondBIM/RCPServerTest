namespace RevitSketchPoC.Phase1_VectorExtraction.Models.Geometry
{
    public sealed class EntityStyle
    {
        public double StrokeWidthPt { get; set; }
        public double[]? StrokeColor { get; set; }
        public double[]? FillColor { get; set; }
        public double Opacity { get; set; } = 1.0;
        public double[] DashPattern { get; set; } = System.Array.Empty<double>();
        public int ZOrder { get; set; }
        public string? LayerHint { get; set; }
        public int DrawingIndex { get; set; }
        public bool HasFill { get; set; }
        public bool HasStroke { get; set; } = true;
    }
}
