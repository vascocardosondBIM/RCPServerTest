using System.Collections.Generic;

namespace RevitSketchPoC.Phase1_VectorExtraction.Contracts
{
    /// <summary>Uma zona desenhada pelo utilizador (coordenadas normalizadas 0–1 sobre o preview).</summary>
    public sealed class PageRegionDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        /// <summary>[x0, y0, x1, y1] em 0–1 relativamente à imagem preview (mesmo espaço que clean derotado).</summary>
        public double[] BboxNorm { get; set; } = { 0, 0, 1, 1 };
    }

    public sealed class PageRegionsExportRequest
    {
        public string OutputRoot { get; set; } = string.Empty;
        public IReadOnlyList<PageRegionDefinition> Regions { get; set; } = new List<PageRegionDefinition>();
    }

    public sealed class PageRegionsExportResult
    {
        public string PageRegionsJsonPath { get; set; } = string.Empty;
        public IReadOnlyList<string> RegionIds { get; set; } = new List<string>();
        public int TotalEntitiesExported { get; set; }
    }
}
