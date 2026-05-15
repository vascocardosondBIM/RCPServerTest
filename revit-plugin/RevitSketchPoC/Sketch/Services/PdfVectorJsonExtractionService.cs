using RevitSketchPoC.Phase1_VectorExtraction.Contracts;
using RevitSketchPoC.Phase1_VectorExtraction.Services;
using System;
using System.IO;

namespace RevitSketchPoC.Sketch.Services
{
    public sealed class PdfVectorJsonExtractionResult
    {
        public string RawJsonPath { get; set; } = string.Empty;
        public string CleanJsonPath { get; set; } = string.Empty;
        public string SemanticReadyManifestPath { get; set; } = string.Empty;
        public string SemanticPixelsPath { get; set; } = string.Empty;
        public string TilesDirectoryPath { get; set; } = string.Empty;
        public string CleanJsonPreview { get; set; } = string.Empty;
        public string OutputRoot { get; set; } = string.Empty;
        public string TopologyJsonPath { get; set; } = string.Empty;
        public string ProjectJsonPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Facade retrocompatível — delega para Phase1_VectorExtraction.
    /// </summary>
    public static class PdfVectorJsonExtractionService
    {
        public static PdfVectorJsonExtractionResult Extract(
            string pdfPath,
            int pageNumber,
            int tileSizePt,
            int rasterDpi)
        {
            var phase1 = Phase1VectorExtractionOrchestrator.Extract(new Phase1ExtractionRequest
            {
                PdfPath = pdfPath,
                PdfPageNumber = pageNumber,
                TileSizePt = tileSizePt,
                AiRasterDpi = rasterDpi,
                PreviewRasterDpi = rasterDpi >= 300 ? 200 : 150,
                OcrRasterDpi = Math.Max(rasterDpi, 300),
                UltraRasterDpi = Math.Max(rasterDpi, 400)
            });

            return new PdfVectorJsonExtractionResult
            {
                RawJsonPath = phase1.RawJsonPath,
                CleanJsonPath = phase1.CleanJsonPath,
                SemanticReadyManifestPath = phase1.SemanticReadyManifestPath,
                SemanticPixelsPath = phase1.SemanticPixelsPath,
                TilesDirectoryPath = phase1.TilesDirectoryPath,
                CleanJsonPreview = phase1.CleanJsonPreview,
                OutputRoot = phase1.OutputRoot,
                TopologyJsonPath = phase1.TopologyJsonPath,
                ProjectJsonPath = phase1.ProjectJsonPath
            };
        }
    }
}
