using RevitSketchPoC.Phase1_VectorExtraction.Configuration;
using RevitSketchPoC.Phase1_VectorExtraction.Contracts;
using RevitSketchPoC.Phase1_VectorExtraction.Services.Pdf;
using RevitSketchPoC.Phase1_VectorExtraction.Utils;
using RevitSketchPoC.Sketch.Services;
using System;
using System.IO;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services
{
    /// <summary>
    /// Fase 1: extração PyMuPDF → <c>raw.json</c> legado + árvore JSON modular (<c>phase1_index.json</c>).
    /// </summary>
    public static class Phase1VectorExtractionOrchestrator
    {
        public static Phase1ExtractionResult Extract(Phase1ExtractionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.PdfPath) || !File.Exists(request.PdfPath))
            {
                throw new FileNotFoundException("PDF não encontrado.", request.PdfPath);
            }

            var page = request.PdfPageNumber < 1 ? 1 : request.PdfPageNumber;
            var workDir = Path.Combine(Path.GetTempPath(), "RevitSketchPoC", "phase1");
            Directory.CreateDirectory(workDir);

            var safeName = Path.GetFileNameWithoutExtension(request.PdfPath);
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(c, '_');
            }

            var outputRoot = Path.Combine(
                workDir,
                safeName + "_page" + page + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));

            Directory.CreateDirectory(outputRoot);

            var scriptPath = Phase1ExtractionScriptProvider.EnsureScriptOnDisk(
                Path.Combine(workDir, "scripts"));

            Phase1PythonExtractionRunner.RunModularPhase1(request.PdfPath, outputRoot, page, scriptPath);

            var resolved = Phase1IndexResolver.Resolve(outputRoot);
            if (string.IsNullOrWhiteSpace(resolved.SemanticPixelsPath) ||
                string.IsNullOrWhiteSpace(resolved.SemanticReadyManifestPath))
            {
                throw new InvalidOperationException(
                    "phase1_index.json não referencia semantic_pixels ou semantic_manifest.");
            }

            SemanticPixelsValidator.ValidateTemplate(resolved.SemanticPixelsPath, resolved.SemanticReadyManifestPath);

            var rawPath = Path.Combine(outputRoot, Phase1ArtifactLayout.RawRootLegacy);
            string previewPath = File.Exists(resolved.CleanJsonPath) ? resolved.CleanJsonPath : rawPath;
            var preview = PythonProcessRunner.ReadTextPreview(previewPath, 30000);
            var tilesDir = Path.Combine(outputRoot, Phase1ArtifactLayout.RasterTilesDir);

            return new Phase1ExtractionResult
            {
                OutputRoot = outputRoot,
                IndexJsonPath = resolved.IndexPath,
                RawJsonPath = rawPath,
                CleanJsonPath = resolved.CleanJsonPath,
                TopologyJsonPath = resolved.TopologyIntersectionsPath,
                GraphJsonPath = resolved.TopologyAdjacencyPath,
                SemanticReadyManifestPath = resolved.SemanticReadyManifestPath,
                SemanticPixelsPath = resolved.SemanticPixelsPath,
                TilesDirectoryPath = tilesDir,
                ProjectJsonPath = resolved.ProjectJsonPath,
                CleanJsonPreview = preview
            };
        }
    }
}
