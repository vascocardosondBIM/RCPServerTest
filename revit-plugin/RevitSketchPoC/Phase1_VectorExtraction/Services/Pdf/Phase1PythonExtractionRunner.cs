using RevitSketchPoC.Phase1_VectorExtraction.Configuration;
using RevitSketchPoC.Phase1_VectorExtraction.Utils;
using System;
using System.IO;
using System.Linq;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services.Pdf
{
    public static class Phase1PythonExtractionRunner
    {
        /// <summary>Extração vetor + texto + artefactos JSON modulares; deve caber em poucos minutos na maioria dos PDFs.</summary>
        private const int ExtractionTimeoutMs = 20 * 60 * 1000;

        /// <summary>
        /// Gera <c>raw.json</c> (legado), <c>phase1_index.json</c>, pastas modular e restantes artefactos (ver Fase 1 README).
        /// Argumentos: pdf_path, output_dir, page_number (base 1).
        /// </summary>
        public static void RunModularPhase1(string pdfPath, string outputRoot, int pageNumber, string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                throw new FileNotFoundException("PDF não encontrado.", pdfPath);
            }

            var page = pageNumber < 1 ? 1 : pageNumber;
            var args =
                "\"" + scriptPath + "\" " +
                "\"" + pdfPath + "\" " +
                "\"" + outputRoot + "\" " +
                page;

            var stdout = PythonProcessRunner.RunReturningStdout(
                args,
                ExtractionTimeoutMs,
                "Falha na extração Fase 1 (PyMuPDF).");

            var rawPath = Path.Combine(outputRoot, Phase1ArtifactLayout.RawRootLegacy);
            if (!File.Exists(rawPath))
            {
                throw new InvalidOperationException("raw.json não foi criado em: " + outputRoot);
            }

            var indexPath = Path.Combine(outputRoot, Phase1ArtifactLayout.IndexFileName);
            if (!File.Exists(indexPath))
            {
                var hint = stdout
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .LastOrDefault(l =>
                        l.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(l));
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    indexPath = hint!;
                }
            }

            if (!File.Exists(indexPath))
            {
                throw new InvalidOperationException(
                    "phase1_index.json não foi criado em: " + outputRoot +
                    ". Confirma que o script phase1_extract.py está atualizado.");
            }
        }
    }
}
