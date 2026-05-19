using Newtonsoft.Json.Linq;
using RevitSketchPoC.Phase1_VectorExtraction.Configuration;
using RevitSketchPoC.Phase1_VectorExtraction.Contracts;
using RevitSketchPoC.Phase1_VectorExtraction.Services;
using RevitSketchPoC.Phase1_VectorExtraction.Services.Pdf;
using RevitSketchPoC.Phase1_VectorExtraction.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services.Regions
{
    public sealed class PageRegionColorExportResult
    {
        public string RegionId { get; set; } = string.Empty;
        public string ByColorRoot { get; set; } = string.Empty;
        public string ManifestPath { get; set; } = string.Empty;
        public IReadOnlyList<string> ColorHexKeys { get; set; } = Array.Empty<string>();
    }

    public static class PageRegionColorExportService
    {
        private const int TimeoutMs = 10 * 60 * 1000;
        private const int DefaultDpi = 300;
        private const int DefaultColorTolerance = 32;

        public static IReadOnlyList<PageRegionColorExportResult> ExportAllRegions(
            string outputRoot,
            int previewDpi = DefaultDpi,
            int colorTolerance = DefaultColorTolerance)
        {
            var regionsPath = Path.Combine(outputRoot, Phase1ArtifactLayout.PageRegionsFileName);
            if (!File.Exists(regionsPath))
            {
                throw new FileNotFoundException(
                    "Exporta as zonas primeiro (page_regions.json em falta).",
                    regionsPath);
            }

            var doc = JObject.Parse(File.ReadAllText(regionsPath));
            if (doc["regions"] is not JArray regions)
            {
                throw new InvalidOperationException("page_regions.json sem array \"regions\".");
            }

            var results = new List<PageRegionColorExportResult>();
            foreach (var token in regions)
            {
                if (token is not JObject obj)
                {
                    continue;
                }

                var id = obj["id"]?.ToString();
                var bbox = obj["bbox_pt"]?.ToObject<double[]>();
                if (string.IsNullOrWhiteSpace(id) || bbox == null || bbox.Length < 4)
                {
                    continue;
                }

                results.Add(ExportRegion(outputRoot, id, bbox, previewDpi, colorTolerance));
            }

            return results;
        }

        public static PageRegionColorExportResult ExportRegion(
            string outputRoot,
            string regionId,
            double[] bboxPt,
            int previewDpi = DefaultDpi,
            int colorTolerance = DefaultColorTolerance)
        {
            if (bboxPt == null || bboxPt.Length < 4)
            {
                throw new ArgumentException("bbox_pt inválido.");
            }

            var pdfPath = ResolveSourcePdf(outputRoot);
            var dims = PageRegionExportService.ReadPageDimensions(outputRoot);
            var pageNum = ResolvePageNumber(outputRoot);

            var regionDir = Path.Combine(outputRoot, Phase1ArtifactLayout.RegionsRootDir, regionId);
            Directory.CreateDirectory(regionDir);

            var byColorDir = Path.Combine(regionDir, "by_color");
            Directory.CreateDirectory(byColorDir);

            var workDir = Path.Combine(Path.GetTempPath(), "RevitSketchPoC", "phase1", "scripts");
            var scriptPath = Phase1ZoneByColorScriptProvider.EnsureScriptOnDisk(workDir);

            var inv = CultureInfo.InvariantCulture;
            var args =
                "\"" + scriptPath + "\" " +
                "\"" + pdfPath + "\" " +
                "\"" + byColorDir + "\" " +
                pageNum.ToString(inv) + " " +
                bboxPt[0].ToString(inv) + " " +
                bboxPt[1].ToString(inv) + " " +
                bboxPt[2].ToString(inv) + " " +
                bboxPt[3].ToString(inv) + " " +
                dims.RotationDegrees.ToString(inv) + " " +
                dims.WidthPt.ToString(inv) + " " +
                dims.HeightPt.ToString(inv) + " " +
                previewDpi.ToString(inv) + " " +
                colorTolerance.ToString(inv);

            PythonProcessRunner.RunReturningStdout(args, TimeoutMs, "Falha na exportação por cor (PyMuPDF).");

            var manifestPath = Path.Combine(byColorDir, "by_color_manifest.json");
            var colorKeys = new List<string>();
            if (File.Exists(manifestPath))
            {
                var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                if (manifest["colors"] is JArray colors)
                {
                    foreach (var c in colors)
                    {
                        var hex = c["hex"]?.ToString()?.TrimStart('#');
                        if (!string.IsNullOrWhiteSpace(hex))
                        {
                            colorKeys.Add(hex);
                        }
                    }
                }
            }

            return new PageRegionColorExportResult
            {
                RegionId = regionId,
                ByColorRoot = byColorDir,
                ManifestPath = manifestPath,
                ColorHexKeys = colorKeys
            };
        }

        private static string ResolveSourcePdf(string outputRoot)
        {
            var rawPath = Path.Combine(outputRoot, Phase1ArtifactLayout.RawRootLegacy);
            if (File.Exists(rawPath))
            {
                var raw = JObject.Parse(File.ReadAllText(rawPath));
                var pdf = raw["source_pdf"]?.ToString();
                if (!string.IsNullOrWhiteSpace(pdf) && File.Exists(pdf))
                {
                    return pdf;
                }
            }

            throw new FileNotFoundException(
                "source_pdf não encontrado em raw.json. Gera a Fase 1 novamente.");
        }

        private static int ResolvePageNumber(string outputRoot)
        {
            try
            {
                var clean = Phase1IndexResolver.Resolve(outputRoot).CleanJsonPath;
                if (File.Exists(clean))
                {
                    var page = JObject.Parse(File.ReadAllText(clean))["page"];
                    var n = page?["page_number"]?.Value<int>();
                    if (n.HasValue && n.Value > 0)
                    {
                        return n.Value;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return 1;
        }
    }
}
