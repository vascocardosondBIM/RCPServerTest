using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Phase1_VectorExtraction.Configuration;
using RevitSketchPoC.Phase1_VectorExtraction.Contracts;
using RevitSketchPoC.Phase1_VectorExtraction.Services;
using RevitSketchPoC.Phase1_VectorExtraction.Services.Geometry;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services.Regions
{
    public static class PageRegionExportService
    {
        private static readonly string[] ModularEntityFiles =
        {
            Phase1ArtifactLayout.GeometryLines,
            Phase1ArtifactLayout.GeometryPolylines,
            Phase1ArtifactLayout.GeometryBeziers,
            Phase1ArtifactLayout.GeometryHatches,
            "geometry/rectangles.json",
            Phase1ArtifactLayout.TextWords,
            Phase1ArtifactLayout.TextBlocks,
            Phase1ArtifactLayout.TextSpans,
            Phase1ArtifactLayout.TopologyIntersections,
            Phase1ArtifactLayout.TopologyAdjacency
        };

        public static PageDimensions ReadPageDimensions(string outputRoot)
        {
            var resolved = Phase1IndexResolver.Resolve(outputRoot);
            if (File.Exists(resolved.CleanJsonPath))
            {
                var clean = JObject.Parse(File.ReadAllText(resolved.CleanJsonPath));
                var page = clean["page"];
                if (page != null)
                {
                    return new PageDimensions(
                        page["width_pt"]?.Value<double>() ?? 0,
                        page["height_pt"]?.Value<double>() ?? 0,
                        page["rotation_degrees"]?.Value<int>() ?? 0);
                }
            }

            if (File.Exists(resolved.RawJsonPath))
            {
                var raw = JObject.Parse(File.ReadAllText(resolved.RawJsonPath));
                var page = raw["page"];
                if (page != null)
                {
                    return new PageDimensions(
                        page["width_pt"]?.Value<double>() ?? 0,
                        page["height_pt"]?.Value<double>() ?? 0,
                        page["rotation_degrees"]?.Value<int>() ?? 0);
                }
            }

            throw new InvalidOperationException("Não foi possível ler width_pt/height_pt do output da Fase 1.");
        }

        public static string GetPreviewPngPath(string outputRoot)
        {
            var path = Path.Combine(outputRoot, Phase1ArtifactLayout.PreviewPagePngRelative());
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Preview PNG não encontrado. Corre a Fase 1 primeiro.", path);
            }

            return path;
        }

        public static PageRegionsExportResult Export(PageRegionsExportRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.OutputRoot) || !Directory.Exists(request.OutputRoot))
            {
                throw new DirectoryNotFoundException("Output root inválido: " + request.OutputRoot);
            }

            if (request.Regions == null || request.Regions.Count == 0)
            {
                throw new InvalidOperationException("Define pelo menos uma zona antes de exportar.");
            }

            var dims = ReadPageDimensions(request.OutputRoot);
            var previewPath = GetPreviewPngPath(request.OutputRoot);
            var resolved = Phase1IndexResolver.Resolve(request.OutputRoot);
            var regionSummaries = new List<JObject>();
            var totalEntities = 0;

            foreach (var region in request.Regions)
            {
                var regionId = SanitizeRegionId(region.Id, region.Label);
                var bboxPt = NormToPt(region.BboxNorm, dims.WidthPt, dims.HeightPt);
                var regionDir = Path.Combine(request.OutputRoot, Phase1ArtifactLayout.RegionsRootDir, regionId);
                Directory.CreateDirectory(regionDir);

                CropPreviewToRegion(previewPath, region.BboxNorm, Path.Combine(regionDir, "page.png"));

                var entityCount = 0;
                foreach (var rel in ModularEntityFiles)
                {
                    var src = Path.Combine(request.OutputRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(src))
                    {
                        continue;
                    }

                    entityCount += FilterAndWriteEntityFile(src, regionDir, rel, bboxPt);
                }

                WriteRegionCleanSlice(resolved.CleanJsonPath, regionDir, regionId, region.Label, bboxPt, dims);
                totalEntities += entityCount;

                regionSummaries.Add(new JObject
                {
                    ["id"] = regionId,
                    ["label"] = region.Label ?? regionId,
                    ["bbox_norm"] = new JArray(region.BboxNorm),
                    ["bbox_pt"] = new JArray(bboxPt),
                    ["output_dir"] = Path.Combine(Phase1ArtifactLayout.RegionsRootDir, regionId).Replace('\\', '/'),
                    ["entity_files_exported"] = entityCount
                });
            }

            var doc = new JObject
            {
                ["schema"] = "phase1.page_regions.v1",
                ["generated_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["coordinate_space"] = "derotated_page_space",
                ["source_preview"] = Phase1ArtifactLayout.PreviewPagePngRelative(),
                ["page"] = new JObject
                {
                    ["width_pt"] = dims.WidthPt,
                    ["height_pt"] = dims.HeightPt,
                    ["rotation_degrees"] = dims.RotationDegrees
                },
                ["regions"] = new JArray(regionSummaries)
            };

            var regionsJsonPath = Path.Combine(request.OutputRoot, Phase1ArtifactLayout.PageRegionsFileName);
            File.WriteAllText(regionsJsonPath, doc.ToString(Formatting.Indented));
            UpdateIndexWithRegions(request.OutputRoot, regionSummaries);

            return new PageRegionsExportResult
            {
                PageRegionsJsonPath = regionsJsonPath,
                RegionIds = request.Regions.Select(r => SanitizeRegionId(r.Id, r.Label)).ToList(),
                TotalEntitiesExported = totalEntities
            };
        }

        private static void UpdateIndexWithRegions(string outputRoot, List<JObject> regionSummaries)
        {
            var indexPath = Path.Combine(outputRoot, Phase1ArtifactLayout.IndexFileName);
            if (!File.Exists(indexPath))
            {
                return;
            }

            var index = JObject.Parse(File.ReadAllText(indexPath));
            if (index["paths"] is not JObject paths)
            {
                paths = new JObject();
                index["paths"] = paths;
            }

            paths["page_regions"] = Phase1ArtifactLayout.PageRegionsFileName;
            var regionPaths = new JObject();
            foreach (var r in regionSummaries)
            {
                var id = r["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                regionPaths[id] = (r["output_dir"]?.ToString() ?? string.Empty).Replace('\\', '/');
            }

            paths["regions"] = regionPaths;
            File.WriteAllText(indexPath, index.ToString(Formatting.Indented));
        }

        private static int FilterAndWriteEntityFile(string sourcePath, string regionDir, string relativePath, double[] bboxPt)
        {
            var root = JObject.Parse(File.ReadAllText(sourcePath));
            var key = ResolveEntitiesKey(root);
            if (key == null || root[key] is not JArray arr)
            {
                return 0;
            }

            var filtered = new JArray();
            foreach (var token in arr)
            {
                if (token is JObject obj && EntityMatchesRegion(obj, bboxPt))
                {
                    filtered.Add(obj);
                }
            }

            var destRel = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.Combine(regionDir, destRel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? regionDir);
            root[key] = filtered;
            root["region_filter"] = new JObject
            {
                ["bbox_pt"] = new JArray(bboxPt),
                ["assignment"] = "bbox_intersects"
            };
            File.WriteAllText(dest, root.ToString(Formatting.Indented));
            return filtered.Count;
        }

        private static string? ResolveEntitiesKey(JObject root)
        {
            if (root["entities"] is JArray)
            {
                return "entities";
            }

            foreach (var prop in root.Properties())
            {
                if (prop.Value is JArray && prop.Name is not "bbox_norm" and not "bbox_pt")
                {
                    return prop.Name;
                }
            }

            return null;
        }

        private static bool EntityMatchesRegion(JObject obj, double[] bboxPt)
        {
            var entityBbox = TryReadEntityBbox(obj);
            if (entityBbox == null)
            {
                return false;
            }

            return GeometryOperationsService.BboxIntersects(bboxPt, entityBbox);
        }

        private static double[]? TryReadEntityBbox(JObject obj)
        {
            if (obj["bbox"] is JObject bd)
            {
                return new[]
                {
                    bd["x_min"]?.Value<double>() ?? 0,
                    bd["y_min"]?.Value<double>() ?? 0,
                    bd["x_max"]?.Value<double>() ?? 0,
                    bd["y_max"]?.Value<double>() ?? 0
                };
            }

            if (obj["bbox_pt"] is JArray pt && pt.Count >= 4)
            {
                return new[]
                {
                    pt[0]!.Value<double>(),
                    pt[1]!.Value<double>(),
                    pt[2]!.Value<double>(),
                    pt[3]!.Value<double>()
                };
            }

            if (obj["point"] is JArray point && point.Count >= 2)
            {
                var x = point[0]!.Value<double>();
                var y = point[1]!.Value<double>();
                return new[] { x, y, x, y };
            }

            return null;
        }

        private static void WriteRegionCleanSlice(
            string cleanPath,
            string regionDir,
            string regionId,
            string? label,
            double[] bboxPt,
            PageDimensions dims)
        {
            if (!File.Exists(cleanPath))
            {
                return;
            }

            var clean = JObject.Parse(File.ReadAllText(cleanPath));
            var page = clean["page"] as JObject;
            if (page?["geometry"] is not JObject geom)
            {
                return;
            }

            var lines = geom["lines"] as JArray;
            var rects = geom["rectangles"] as JArray;
            if (lines != null)
            {
                geom["lines"] = FilterCleanLines(lines, bboxPt);
            }

            if (rects != null)
            {
                geom["rectangles"] = FilterCleanRectangles(rects, bboxPt);
            }

            clean["region_id"] = regionId;
            clean["region_label"] = label ?? regionId;
            clean["region_bbox_pt"] = new JArray(bboxPt);
            var dest = Path.Combine(regionDir, "clean_slice.json");
            File.WriteAllText(dest, clean.ToString(Formatting.Indented));
        }

        private static JArray FilterCleanLines(JArray lines, double[] bboxPt)
        {
            var result = new JArray();
            foreach (var token in lines)
            {
                if (token is not JObject ln)
                {
                    continue;
                }

                var from = ln["from"];
                var to = ln["to"];
                if (from == null || to == null)
                {
                    continue;
                }

                var x0 = Math.Min(from["x"]!.Value<double>(), to["x"]!.Value<double>());
                var y0 = Math.Min(from["y"]!.Value<double>(), to["y"]!.Value<double>());
                var x1 = Math.Max(from["x"]!.Value<double>(), to["x"]!.Value<double>());
                var y1 = Math.Max(from["y"]!.Value<double>(), to["y"]!.Value<double>());
                if (GeometryOperationsService.BboxIntersects(bboxPt, new[] { x0, y0, x1, y1 }))
                {
                    result.Add(ln);
                }
            }

            return result;
        }

        private static JArray FilterCleanRectangles(JArray rects, double[] bboxPt)
        {
            var result = new JArray();
            foreach (var token in rects)
            {
                if (token is not JObject rc || rc["bbox_pt"] is not JArray bb || bb.Count < 4)
                {
                    continue;
                }

                var b = new[]
                {
                    bb[0]!.Value<double>(),
                    bb[1]!.Value<double>(),
                    bb[2]!.Value<double>(),
                    bb[3]!.Value<double>()
                };
                if (GeometryOperationsService.BboxIntersects(bboxPt, b))
                {
                    result.Add(rc);
                }
            }

            return result;
        }

        private static void CropPreviewToRegion(string previewPath, double[] norm, string destPath)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(previewPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var w = bitmap.PixelWidth;
            var h = bitmap.PixelHeight;
            var x0 = (int)Math.Floor(Clamp01(norm[0]) * w);
            var y0 = (int)Math.Floor(Clamp01(norm[1]) * h);
            var x1 = (int)Math.Ceiling(Clamp01(norm[2]) * w);
            var y1 = (int)Math.Ceiling(Clamp01(norm[3]) * h);
            x1 = Math.Max(x1, x0 + 1);
            y1 = Math.Max(y1, y0 + 1);
            x1 = Math.Min(x1, w);
            y1 = Math.Min(y1, h);

            var crop = new CroppedBitmap(bitmap, new Int32Rect(x0, y0, x1 - x0, y1 - y0));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(crop));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? string.Empty);
            using var stream = File.Create(destPath);
            encoder.Save(stream);
        }

        private static double[] NormToPt(double[] norm, double widthPt, double heightPt)
        {
            return new[]
            {
                Clamp01(norm[0]) * widthPt,
                Clamp01(norm[1]) * heightPt,
                Clamp01(norm[2]) * widthPt,
                Clamp01(norm[3]) * heightPt
            };
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private static string SanitizeRegionId(string id, string? label)
        {
            var raw = string.IsNullOrWhiteSpace(id) ? label : id;
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = "zone";
            }

            var chars = raw.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray();
            var s = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(s) ? "zone" : s;
        }

        public sealed class PageDimensions
        {
            public PageDimensions(double widthPt, double heightPt, int rotationDegrees)
            {
                WidthPt = widthPt;
                HeightPt = heightPt;
                RotationDegrees = rotationDegrees;
            }

            public double WidthPt { get; }
            public double HeightPt { get; }
            public int RotationDegrees { get; }
        }
    }
}
