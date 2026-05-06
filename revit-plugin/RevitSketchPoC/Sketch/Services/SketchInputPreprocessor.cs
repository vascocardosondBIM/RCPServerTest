using RevitSketchPoC.Sketch.Contracts;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Normalizes sketch inputs before sending them to the LLM.
    /// Supports direct image input and PDF-to-image conversion (first page).
    /// </summary>
    public static class SketchInputPreprocessor
    {
        private const int DefaultPdfRenderDpi = 300;

        public static SketchToBimRequest NormalizeForLlm(SketchToBimRequest request, Action<string>? log = null)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!string.IsNullOrWhiteSpace(request.ImageBase64))
            {
                return request;
            }

            if (string.IsNullOrWhiteSpace(request.ImagePath))
            {
                return request;
            }

            var path = request.ImagePath.Trim();
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return request;
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("PDF file not found.", path);
            }

            var requestedPageNumber = request.PdfPageNumber < 1 ? 1 : request.PdfPageNumber;
            log?.Invoke("PDF detetado. A converter a página " + requestedPageNumber + " para imagem (PNG) para o LLM...");
            var renderedImagePath = ConvertPdfPageToPng(path, requestedPageNumber);
            log?.Invoke("PDF convertido com sucesso: " + renderedImagePath);

            return CloneWithNewImagePath(request, renderedImagePath);
        }

        private static SketchToBimRequest CloneWithNewImagePath(SketchToBimRequest source, string imagePath)
        {
            return new SketchToBimRequest
            {
                ImagePath = imagePath,
                ImageBase64 = null,
                MimeType = "image/png",
                Prompt = source.Prompt,
                TargetLevelName = source.TargetLevelName,
                WallTypeName = source.WallTypeName,
                AutoCreateRooms = source.AutoCreateRooms,
                AutoCreateDoors = source.AutoCreateDoors,
                ShowPreviewUi = source.ShowPreviewUi,
                PdfPageNumber = source.PdfPageNumber
            };
        }

        private static string ConvertPdfPageToPng(string pdfPath, int pageNumber)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "RevitSketchPoC", "pdf-renders");
            Directory.CreateDirectory(tempDir);

            var safeName = Path.GetFileNameWithoutExtension(pdfPath);
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(c, '_');
            }

            var outputPath = Path.Combine(
                tempDir,
                safeName + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + ".png");

            var script = string.Join(
                ";",
                "import fitz,sys",
                "pdf=sys.argv[1]",
                "out=sys.argv[2]",
                "page_index=int(sys.argv[3])",
                "dpi=int(sys.argv[4])",
                "doc=fitz.open(pdf)",
                "page=doc.load_page(page_index)",
                "pix=page.get_pixmap(dpi=dpi)",
                "pix.save(out)",
                "doc.close()",
                "print(out)"
            );

            var args =
                "-c \"" + script.Replace("\"", "\\\"") + "\" " +
                "\"" + pdfPath + "\" " +
                "\"" + outputPath + "\" " +
                Math.Max(0, pageNumber - 1) + " " +
                DefaultPdfRenderDpi;

            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();

            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        stdOut.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        stdErr.AppendLine(e.Data);
                    }
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException("Falha ao iniciar o Python para converter o PDF.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                const int timeoutMs = 120000;
                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // ignored
                    }

                    throw new InvalidOperationException("Timeout ao converter PDF para imagem (120s).");
                }

                if (process.ExitCode != 0 || !File.Exists(outputPath))
                {
                    var error = stdErr.ToString().Trim();
                    var output = stdOut.ToString().Trim();
                    throw new InvalidOperationException(
                        "Não foi possível converter o PDF para imagem.\n" +
                        "Confirma que tens Python e PyMuPDF instalados (`python -m pip install pymupdf`).\n" +
                        "Também confirma se a página pedida existe no PDF (pdfPageNumber=" + pageNumber + ").\n" +
                        "stderr: " + (string.IsNullOrWhiteSpace(error) ? "(vazio)" : error) + "\n" +
                        "stdout: " + (string.IsNullOrWhiteSpace(output) ? "(vazio)" : output));
                }
            }

            return outputPath;
        }
    }
}
