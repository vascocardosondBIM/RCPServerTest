using System.IO;

namespace RevitSketchPoC.Phase1_VectorExtraction.Configuration
{
    /// <summary>
    /// Pastas e nomes relativos ao output root da Fase 1 (contrato estável para o pipeline).
    /// </summary>
    public static class Phase1ArtifactLayout
    {
        public const string IndexFileName = "phase1_index.json";
        public const string RawRootLegacy = "raw.json";
        public const string MetadataProject = "metadata/project.json";
        public const string GeometryLines = "geometry/lines.json";
        public const string GeometryPolylines = "geometry/polylines.json";
        public const string GeometryBeziers = "geometry/beziers.json";
        public const string GeometryHatches = "geometry/hatches.json";
        public const string TextWords = "text/words.json";
        public const string TextBlocks = "text/blocks.json";
        public const string TextSpans = "text/spans.json";
        public const string TopologyIntersections = "topology/intersections.json";
        public const string TopologyAdjacency = "topology/adjacency.json";
        public const string RasterPreviewDir = "raster/preview";
        public const string RasterAiDir = "raster/ai";
        public const string RasterOcrDir = "raster/ocr";
        public const string RasterTilesDir = "raster/tiles";
        public const string SemanticManifest = "semantic/semantic_ready_manifest.json";
        public const string SemanticPixels = "semantic/semantic_pixels.json";

        public static string RawPackagedRelative(string runBase) =>
            Path.Combine("raw", runBase + "_raw.json");

        public static string CleanPackagedRelative(string runBase) =>
            Path.Combine("clean", runBase + "_clean.json");

        public static string PreviewPagePngRelative() =>
            Path.Combine(RasterPreviewDir, "page.png");
    }
}
