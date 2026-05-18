namespace RevitSketchPoC.Phase1_VectorExtraction.Contracts
{
    /// <summary>
    /// Artefactos gerados pela Fase 1. Propriedades legacy mantêm compatibilidade com o pipeline semântico.
    /// </summary>
    public sealed class Phase1ExtractionResult
    {
        public string OutputRoot { get; set; } = string.Empty;
        /// <summary><c>phase1_index.json</c> na raiz do output (mapa de artefactos).</summary>
        public string IndexJsonPath { get; set; } = string.Empty;
        public string RawJsonPath { get; set; } = string.Empty;
        public string CleanJsonPath { get; set; } = string.Empty;
        public string TopologyJsonPath { get; set; } = string.Empty;
        public string GraphJsonPath { get; set; } = string.Empty;
        public string SemanticReadyManifestPath { get; set; } = string.Empty;
        public string SemanticPixelsPath { get; set; } = string.Empty;
        public string TilesDirectoryPath { get; set; } = string.Empty;
        public string ProjectJsonPath { get; set; } = string.Empty;
        public string PreviewPngPath { get; set; } = string.Empty;
        public string ParquetDirectoryPath { get; set; } = string.Empty;
        public string CleanJsonPreview { get; set; } = string.Empty;
    }
}
