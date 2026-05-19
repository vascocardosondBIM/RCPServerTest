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
                Adjacency = CountJsonArray(baseDir, Phase1ArtifactLayout.TopologyAdjacency, "adjacency")
            };
            return c;
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
