namespace RevitSketchPoC.Phase1_VectorExtraction.Models.Geometry
{
    public sealed class LineEntity
    {
        public string Id { get; set; } = string.Empty;
        public Point2D From { get; set; } = new Point2D();
        public Point2D To { get; set; } = new Point2D();
        public double[] BboxPt { get; set; } = System.Array.Empty<double>();
        public EntityStyle Style { get; set; } = new EntityStyle();
        public string[] TopologyRefs { get; set; } = System.Array.Empty<string>();
        public string? SemanticHint { get; set; }
    }

    public sealed class Point2D
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}
