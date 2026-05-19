using Newtonsoft.Json.Linq;
using RevitSketchPoC.Phase1_VectorExtraction.Configuration;
using RevitSketchPoC.Phase1_VectorExtraction.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.Phase1_VectorExtraction.Services
{
    public static class Phase1ExtractionSummaryService
    {
        private static readonly string[] GeometryColorScanFiles =
        {
            Phase1ArtifactLayout.GeometryLines,
            Phase1ArtifactLayout.GeometryPolylines,
            Phase1ArtifactLayout.GeometryBeziers,
            "geometry/rectangles.json",
            Phase1ArtifactLayout.GeometryHatches
        };

        private const int MaxColorsListed = 16;

        public static Phase1ExtractionSummary BuildFromOutputRoot(string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
            {
                throw new DirectoryNotFoundException("Output root inválido: " + outputRoot);
            }

            var summary = new Phase1ExtractionSummary
            {
                ScopeLabel = "PDF completo (página extraída)",
                FullPage = CountModularArtifacts(outputRoot)
            };

            EnrichFromCleanJson(outputRoot, summary.FullPage);
            EnrichFromRawSummary(outputRoot, summary.FullPage);

            var regionsPath = Path.Combine(outputRoot, Phase1ArtifactLayout.PageRegionsFileName);
            if (File.Exists(regionsPath))
            {
                summary.Regions = LoadRegionSummaries(outputRoot, regionsPath);
            }

            return summary;
        }

        public static string FormatAsText(Phase1ExtractionSummary summary)
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ " + summary.ScopeLabel + " ═══");
            AppendCountsBlock(sb, summary.FullPage, "  ");

            if (summary.Regions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("─── Por zona ───");
                foreach (var region in summary.Regions)
                {
                    sb.AppendLine();
                    sb.Append("▸ ").Append(region.Label);
                    if (!string.Equals(region.Label, region.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(" (").Append(region.Id).Append(')');
                    }

                    sb.AppendLine();
                    AppendCountsBlock(sb, region.Counts, "    ");
                }
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("(Ainda sem zonas exportadas — usa «Definir zonas…»)");
            }

            return sb.ToString().TrimEnd();
        }

        private static void AppendCountsBlock(StringBuilder sb, Phase1ElementCounts c, string indent)
        {
            sb.Append(indent).AppendLine("Geometria (modular):");
            sb.Append(indent).Append("  Linhas: ").AppendLine(c.Lines.ToString());
            sb.Append(indent).Append("  Polilinhas: ").AppendLine(c.Polylines.ToString());
            sb.Append(indent).Append("  Bézier: ").AppendLine(c.Beziers.ToString());
            sb.Append(indent).Append("  Retângulos: ").AppendLine(c.Rectangles.ToString());
            sb.Append(indent).Append("  Hatches: ").AppendLine(c.Hatches.ToString());
            sb.Append(indent).Append("  Subtotal geometria: ").AppendLine(c.GeometryTotal.ToString());

            sb.Append(indent).AppendLine("Texto:");
            sb.Append(indent).Append("  Words: ").AppendLine(c.Words.ToString());
            sb.Append(indent).Append("  Blocos: ").AppendLine(c.TextBlocks.ToString());
            sb.Append(indent).Append("  Spans: ").AppendLine(c.TextSpans.ToString());
            sb.Append(indent).Append("  Subtotal texto: ").AppendLine(c.TextTotal.ToString());

            if (c.Intersections > 0 || c.Adjacency > 0)
            {
                sb.Append(indent).AppendLine("Topologia:");
                sb.Append(indent).Append("  Interseções: ").AppendLine(c.Intersections.ToString());
                sb.Append(indent).Append("  Adjacência: ").AppendLine(c.Adjacency.ToString());
            }

            if (c.CleanLines > 0 || c.CleanRectangles > 0)
            {
                sb.Append(indent).AppendLine("Clean (normalizado):");
                sb.Append(indent).Append("  Linhas: ").AppendLine(c.CleanLines.ToString());
                sb.Append(indent).Append("  Retângulos: ").AppendLine(c.CleanRectangles.ToString());
            }

            sb.Append(indent).Append("Total entidades (modular): ").AppendLine(c.GrandTotal.ToString());
            AppendColorBlock(sb, c.Colors, indent);
        }

        private static void AppendColorBlock(StringBuilder sb, Phase1ColorStatistics colors, string indent)
        {
            if (colors.DistinctCombined == 0 && colors.StrokeColors.Count == 0 && colors.FillColors.Count == 0)
            {
                sb.Append(indent).AppendLine("Cores (geometria): nenhuma cor distinta detectada em style.*");
                return;
            }

            sb.Append(indent).AppendLine("Cores (geometria — stroke/fill em entities):");
            sb.Append(indent).Append("  Cores distintas (traço): ").AppendLine(colors.DistinctStrokeColors.ToString());
            sb.Append(indent).Append("  Cores distintas (preenchimento): ").AppendLine(colors.DistinctFillColors.ToString());
            sb.Append(indent).Append("  Cores distintas (união): ").AppendLine(colors.DistinctCombined.ToString());

            AppendColorList(sb, colors.StrokeColors, indent, "traço");
            AppendColorList(sb, colors.FillColors, indent, "preenchimento");
        }

        private static void AppendColorList(StringBuilder sb, List<Phase1ColorUsage> list, string indent, string kind)
        {
            if (list.Count == 0)
            {
                return;
            }

            sb.Append(indent).Append("  Top ").Append(kind).Append(':').AppendLine();
            foreach (var u in list)
            {
                sb.Append(indent).Append("    ").Append(u.Hex).Append(" (").Append(u.RgbLabel).Append("): ")
                    .Append(u.EntityCount).AppendLine(" entidades");
            }

            if (list.Count >= MaxColorsListed)
            {
                sb.Append(indent).AppendLine("    … (lista limitada a " + MaxColorsListed + " cores)");
            }
        }

        private static Phase1ElementCounts CountModularArtifacts(string baseDir)
        {
            var c = new Phase1ElementCounts
            {
                Lines = CountJsonArray(baseDir, Phase1ArtifactLayout.GeometryLines),
                Polylines = CountJsonArray(baseDir, Phase1ArtifactLayout.GeometryPolylines),
                Beziers = CountJsonArray(baseDir, Phase1ArtifactLayout.GeometryBeziers),
                Rectangles = CountJsonArray(baseDir, "geometry/rectangles.json"),
                Hatches = CountJsonArray(baseDir, Phase1ArtifactLayout.GeometryHatches),
                Words = CountJsonArray(baseDir, Phase1ArtifactLayout.TextWords),
                TextBlocks = CountJsonArray(baseDir, Phase1ArtifactLayout.TextBlocks),
                TextSpans = CountJsonArray(baseDir, Phase1ArtifactLayout.TextSpans),
                Intersections = CountJsonArray(baseDir, Phase1ArtifactLayout.TopologyIntersections, "intersections"),
                Adjacency = CountJsonArray(baseDir, Phase1ArtifactLayout.TopologyAdjacency, "adjacency"),
                Colors = CollectGeometryColors(baseDir)
            };
            return c;
        }

        private static Phase1ColorStatistics CollectGeometryColors(string baseDir)
        {
            var strokeMap = new Dictionary<string, int>(StringComparer.Ordinal);
            var fillMap = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var rel in GeometryColorScanFiles)
            {
                ScanGeometryFileColors(baseDir, rel, strokeMap, fillMap);
            }

            var combined = new HashSet<string>(strokeMap.Keys, StringComparer.Ordinal);
            foreach (var k in fillMap.Keys)
            {
                combined.Add(k);
            }

            return new Phase1ColorStatistics
            {
                DistinctStrokeColors = strokeMap.Count,
                DistinctFillColors = fillMap.Count,
                DistinctCombined = combined.Count,
                StrokeColors = ToSortedUsageList(strokeMap),
                FillColors = ToSortedUsageList(fillMap)
            };
        }

        private static void ScanGeometryFileColors(
            string baseDir,
            string relativePath,
            Dictionary<string, int> strokeMap,
            Dictionary<string, int> fillMap)
        {
            var path = Path.Combine(baseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var key = ResolveEntitiesKey(root);
                if (key == null || root[key] is not JArray entities)
                {
                    return;
                }

                foreach (var token in entities)
                {
                    if (token is not JObject entity)
                    {
                        continue;
                    }

                    if (entity["style"] is not JObject style)
                    {
                        continue;
                    }

                    if (TryRgbKey(style["stroke_color"], out var strokeKey))
                    {
                        strokeMap[strokeKey] = strokeMap.TryGetValue(strokeKey, out var n) ? n + 1 : 1;
                    }

                    if (TryRgbKey(style["fill_color"], out var fillKey))
                    {
                        fillMap[fillKey] = fillMap.TryGetValue(fillKey, out var n) ? n + 1 : 1;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        private static List<Phase1ColorUsage> ToSortedUsageList(Dictionary<string, int> map)
        {
            return map
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(MaxColorsListed)
                .Select(kv => ToUsage(kv.Key, kv.Value))
                .ToList();
        }

        private static Phase1ColorUsage ToUsage(string rgbKey, int count)
        {
            var parts = rgbKey.Split(',');
            var r = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var g = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var b = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            return new Phase1ColorUsage
            {
                RgbKey = rgbKey,
                Hex = "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2"),
                RgbLabel = "rgb " + r + "," + g + "," + b,
                EntityCount = count
            };
        }

        private static bool TryRgbKey(JToken? token, out string key)
        {
            key = string.Empty;
            if (token is not JArray arr || arr.Count < 3)
            {
                return false;
            }

            if (arr[0].Type == JTokenType.Null || arr[1].Type == JTokenType.Null || arr[2].Type == JTokenType.Null)
            {
                return false;
            }

            var v0 = arr[0]!.Value<double>();
            var v1 = arr[1]!.Value<double>();
            var v2 = arr[2]!.Value<double>();

            var scale = v0 <= 1.0 && v1 <= 1.0 && v2 <= 1.0 && v0 >= 0 && v1 >= 0 && v2 >= 0 ? 255.0 : 1.0;
            var r = ClampByte(v0 * scale);
            var g = ClampByte(v1 * scale);
            var b = ClampByte(v2 * scale);
            key = r + "," + g + "," + b;
            return true;
        }

        private static int ClampByte(double v)
        {
            if (v < 0)
            {
                return 0;
            }

            if (v > 255)
            {
                return 255;
            }

            return (int)Math.Round(v);
        }

        private static int CountJsonArray(string baseDir, string relativePath, string? arrayKey = null)
        {
            var path = Path.Combine(baseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                return 0;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var key = arrayKey ?? ResolveEntitiesKey(root);
                if (key == null || root[key] is not JArray arr)
                {
                    return 0;
                }

                return arr.Count;
            }
            catch
            {
                return 0;
            }
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

        private static void EnrichFromCleanJson(string outputRoot, Phase1ElementCounts counts)
        {
            string? cleanPath = null;
            if (File.Exists(Path.Combine(outputRoot, Phase1ArtifactLayout.IndexFileName)))
            {
                try
                {
                    cleanPath = Phase1IndexResolver.Resolve(outputRoot).CleanJsonPath;
                }
                catch
                {
                    // ignore
                }
            }

            if (string.IsNullOrWhiteSpace(cleanPath) || !File.Exists(cleanPath))
            {
                var cleanDir = Path.Combine(outputRoot, "clean");
                if (Directory.Exists(cleanDir))
                {
                    cleanPath = Directory.GetFiles(cleanDir, "*_clean.json").FirstOrDefault();
                }
            }

            if (string.IsNullOrWhiteSpace(cleanPath) || !File.Exists(cleanPath))
            {
                return;
            }

            try
            {
                var clean = JObject.Parse(File.ReadAllText(cleanPath));
                var geom = clean["page"]?["geometry"];
                counts.CleanLines = geom?["lines"] is JArray la ? la.Count : 0;
                counts.CleanRectangles = geom?["rectangles"] is JArray ra ? ra.Count : 0;
            }
            catch
            {
                // ignore
            }
        }

        private static void EnrichFromRawSummary(string outputRoot, Phase1ElementCounts counts)
        {
            var rawPath = Path.Combine(outputRoot, Phase1ArtifactLayout.RawRootLegacy);
            if (!File.Exists(rawPath))
            {
                return;
            }

            try
            {
                var raw = JObject.Parse(File.ReadAllText(rawPath));
                var page = raw["page"];
                if (page == null)
                {
                    return;
                }

                if (counts.Words == 0 && page["text_words"] is JArray tw)
                {
                    counts.Words = tw.Count;
                }

                var geom = page["geometry"];
                if (geom == null)
                {
                    return;
                }

                if (counts.Lines == 0 && geom["lines"] is JArray lines)
                {
                    counts.Lines = lines.Count;
                }

                if (counts.Beziers == 0 && geom["beziers"] is JArray bez)
                {
                    counts.Beziers = bez.Count;
                }

                if (counts.Rectangles == 0 && geom["rectangles"] is JArray rects)
                {
                    counts.Rectangles = rects.Count;
                }
            }
            catch
            {
                // ignore
            }
        }

        private static List<Phase1RegionSummary> LoadRegionSummaries(string outputRoot, string regionsPath)
        {
            var list = new List<Phase1RegionSummary>();
            var doc = JObject.Parse(File.ReadAllText(regionsPath));
            if (doc["regions"] is not JArray regions)
            {
                return list;
            }

            foreach (var token in regions)
            {
                if (token is not JObject obj)
                {
                    continue;
                }

                var id = obj["id"]?.ToString() ?? "zone";
                var label = obj["label"]?.ToString() ?? id;
                var relDir = obj["output_dir"]?.ToString();
                var regionDir = string.IsNullOrWhiteSpace(relDir)
                    ? Path.Combine(outputRoot, Phase1ArtifactLayout.RegionsRootDir, id)
                    : Path.Combine(outputRoot, relDir.Replace('/', Path.DirectorySeparatorChar));

                if (!Directory.Exists(regionDir))
                {
                    continue;
                }

                var counts = CountModularArtifacts(regionDir);
                var cleanSlice = Path.Combine(regionDir, "clean_slice.json");
                if (File.Exists(cleanSlice))
                {
                    try
                    {
                        var clean = JObject.Parse(File.ReadAllText(cleanSlice));
                        var geom = clean["page"]?["geometry"];
                        counts.CleanLines = geom?["lines"] is JArray la ? la.Count : counts.CleanLines;
                        counts.CleanRectangles = geom?["rectangles"] is JArray ra ? ra.Count : counts.CleanRectangles;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                list.Add(new Phase1RegionSummary
                {
                    Id = id,
                    Label = label,
                    Counts = counts
                });
            }

            return list;
        }
    }
}
