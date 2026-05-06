using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Geometric post-processing for semantic detections:
    /// snaps LLM bboxes to closest clean geometry (lines/rectangles).
    /// </summary>
    public static class SemanticGeometryMatcher
    {
        public static SemanticGeometryMatchResult MatchAndPersist(
            string semanticPixelsPath,
            string cleanJsonPath,
            string semanticReadyManifestPath,
            double maxSnapDistancePt = 6.0)
        {
            SemanticPixelsValidator.ValidateTemplate(semanticPixelsPath, semanticReadyManifestPath);

            var semanticRoot = LoadJsonObject(semanticPixelsPath, "semantic_pixels");
            var cleanRoot = LoadJsonObject(cleanJsonPath, "clean");
            var manifestRoot = LoadJsonObject(semanticReadyManifestPath, "semantic_ready_manifest");

            if (semanticRoot["detections"] is not JArray detections)
            {
                throw new InvalidOperationException("semantic_pixels deve conter array \"detections\".");
            }

            var tilesById = ReadTiles(manifestRoot);
            var candidates = ReadGeometryCandidates(cleanRoot);

            var matched = 0;
            var unmatched = 0;

            for (var i = 0; i < detections.Count; i++)
            {
                if (detections[i] is not JObject detection)
                {
                    continue;
                }

                var tileId = detection["tile_id"]?.ToString();
                if (string.IsNullOrWhiteSpace(tileId) || !tilesById.TryGetValue(tileId, out var tile))
                {
                    unmatched++;
                    continue;
                }

                var bboxPx = TryReadBbox(detection["bbox"] as JArray);
                if (bboxPx == null)
                {
                    unmatched++;
                    continue;
                }

                var bboxPt = TileBboxPixelsToPagePoints(bboxPx, tile);
                var best = FindBestCandidate(bboxPt, candidates, maxSnapDistancePt);
                if (best == null)
                {
                    detection["is_snapped"] = false;
                    detection["bbox_pt"] = ToJArray(bboxPt);
                    detection["snap_source"] = "none";
                    unmatched++;
                    continue;
                }

                var snappedPt = best.BboxPt;
                var snappedPx = PagePointsToTileBboxPixels(snappedPt, tile);

                detection["bbox"] = ToJArray(snappedPx);
                detection["bbox_original_pt"] = ToJArray(bboxPt);
                detection["bbox_snapped_pt"] = ToJArray(snappedPt);
                detection["is_snapped"] = true;
                detection["snap_source"] = best.Kind;
                detection["snap_source_index"] = best.SourceIndex;
                detection["snap_score"] = Math.Round(best.Score, 6);
                matched++;
            }

            semanticRoot["matching"] = new JObject
            {
                ["applied"] = true,
                ["method"] = "nearest_iou_or_center_distance",
                ["max_snap_distance_pt"] = Math.Round(maxSnapDistancePt, 4),
                ["candidate_types"] = new JArray("lines", "rectangles"),
                ["matched_count"] = matched,
                ["unmatched_count"] = unmatched
            };

            File.WriteAllText(
                semanticPixelsPath,
                semanticRoot.ToString(Formatting.Indented),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            SemanticPixelsValidator.ValidateTemplate(semanticPixelsPath, semanticReadyManifestPath);
            return new SemanticGeometryMatchResult
            {
                TotalDetections = detections.Count,
                MatchedDetections = matched,
                UnmatchedDetections = unmatched
            };
        }

        private static Dictionary<string, TileInfo> ReadTiles(JObject manifestRoot)
        {
            if (manifestRoot["tiles"] is not JArray tiles || tiles.Count == 0)
            {
                throw new InvalidOperationException("semantic_ready_manifest sem \"tiles\" válidos.");
            }

            var map = new Dictionary<string, TileInfo>(StringComparer.Ordinal);
            foreach (var token in tiles)
            {
                if (token is not JObject tileObj)
                {
                    continue;
                }

                var tileId = tileObj["tile_id"]?.ToString();
                var bboxPt = TryReadBbox(tileObj["bbox_pt"] as JArray);
                var width = tileObj["image_width_px"]?.Value<double?>() ?? 0;
                var height = tileObj["image_height_px"]?.Value<double?>() ?? 0;
                if (string.IsNullOrWhiteSpace(tileId) || bboxPt == null || width <= 0 || height <= 0)
                {
                    continue;
                }

                map[tileId] = new TileInfo(tileId, bboxPt, width, height);
            }

            return map;
        }

        private static List<GeometryCandidate> ReadGeometryCandidates(JObject cleanRoot)
        {
            var list = new List<GeometryCandidate>();

            var lines = cleanRoot["page"]?["geometry"]?["lines"] as JArray;
            if (lines != null)
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i] is not JObject ln)
                    {
                        continue;
                    }

                    var p0 = TryReadPoint(ln["from"] as JObject);
                    var p1 = TryReadPoint(ln["to"] as JObject);
                    if (p0 == null || p1 == null)
                    {
                        continue;
                    }

                    var x0 = Math.Min(p0.Value.X, p1.Value.X);
                    var y0 = Math.Min(p0.Value.Y, p1.Value.Y);
                    var x1 = Math.Max(p0.Value.X, p1.Value.X);
                    var y1 = Math.Max(p0.Value.Y, p1.Value.Y);

                    const double minThicknessPt = 1.5;
                    if (Math.Abs(x1 - x0) < minThicknessPt)
                    {
                        var mid = (x0 + x1) / 2.0;
                        x0 = mid - minThicknessPt / 2.0;
                        x1 = mid + minThicknessPt / 2.0;
                    }

                    if (Math.Abs(y1 - y0) < minThicknessPt)
                    {
                        var mid = (y0 + y1) / 2.0;
                        y0 = mid - minThicknessPt / 2.0;
                        y1 = mid + minThicknessPt / 2.0;
                    }

                    list.Add(new GeometryCandidate("line", i, new[] { x0, y0, x1, y1 }));
                }
            }

            var rectangles = cleanRoot["page"]?["geometry"]?["rectangles"] as JArray;
            if (rectangles != null)
            {
                for (var i = 0; i < rectangles.Count; i++)
                {
                    if (rectangles[i] is not JObject rect)
                    {
                        continue;
                    }

                    var bbox = TryReadBbox(rect["bbox_pt"] as JArray);
                    if (bbox == null)
                    {
                        continue;
                    }

                    list.Add(new GeometryCandidate("rectangle", i, bbox));
                }
            }

            return list;
        }

        private static GeometryCandidate? FindBestCandidate(double[] detectionBboxPt, IReadOnlyList<GeometryCandidate> candidates, double maxSnapDistancePt)
        {
            GeometryCandidate? best = null;
            var bestScore = double.NegativeInfinity;

            foreach (var candidate in candidates)
            {
                var iou = IoU(detectionBboxPt, candidate.BboxPt);
                var centerDistance = CenterDistance(detectionBboxPt, candidate.BboxPt);

                if (iou <= 0 && centerDistance > maxSnapDistancePt)
                {
                    continue;
                }

                var score = iou * 100.0 - centerDistance;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new GeometryCandidate(candidate.Kind, candidate.SourceIndex, candidate.BboxPt, score);
                }
            }

            return best;
        }

        private static double[] TileBboxPixelsToPagePoints(double[] bboxPx, TileInfo tile)
        {
            var tileWidthPt = tile.BboxPt[2] - tile.BboxPt[0];
            var tileHeightPt = tile.BboxPt[3] - tile.BboxPt[1];

            var x0 = tile.BboxPt[0] + Clamp01(bboxPx[0] / tile.WidthPx) * tileWidthPt;
            var y0 = tile.BboxPt[1] + Clamp01(bboxPx[1] / tile.HeightPx) * tileHeightPt;
            var x1 = tile.BboxPt[0] + Clamp01(bboxPx[2] / tile.WidthPx) * tileWidthPt;
            var y1 = tile.BboxPt[1] + Clamp01(bboxPx[3] / tile.HeightPx) * tileHeightPt;

            return NormalizeBbox(x0, y0, x1, y1);
        }

        private static double[] PagePointsToTileBboxPixels(double[] bboxPt, TileInfo tile)
        {
            var tileWidthPt = tile.BboxPt[2] - tile.BboxPt[0];
            var tileHeightPt = tile.BboxPt[3] - tile.BboxPt[1];
            if (tileWidthPt <= 0 || tileHeightPt <= 0)
            {
                return new[] { 0.0, 0.0, 0.0, 0.0 };
            }

            var x0 = Clamp01((bboxPt[0] - tile.BboxPt[0]) / tileWidthPt) * tile.WidthPx;
            var y0 = Clamp01((bboxPt[1] - tile.BboxPt[1]) / tileHeightPt) * tile.HeightPx;
            var x1 = Clamp01((bboxPt[2] - tile.BboxPt[0]) / tileWidthPt) * tile.WidthPx;
            var y1 = Clamp01((bboxPt[3] - tile.BboxPt[1]) / tileHeightPt) * tile.HeightPx;

            return NormalizeBbox(x0, y0, x1, y1);
        }

        private static double IoU(double[] a, double[] b)
        {
            var interX0 = Math.Max(a[0], b[0]);
            var interY0 = Math.Max(a[1], b[1]);
            var interX1 = Math.Min(a[2], b[2]);
            var interY1 = Math.Min(a[3], b[3]);
            var interW = Math.Max(0.0, interX1 - interX0);
            var interH = Math.Max(0.0, interY1 - interY0);
            var inter = interW * interH;
            if (inter <= 0.0)
            {
                return 0.0;
            }

            var areaA = Math.Max(0.0, a[2] - a[0]) * Math.Max(0.0, a[3] - a[1]);
            var areaB = Math.Max(0.0, b[2] - b[0]) * Math.Max(0.0, b[3] - b[1]);
            var union = areaA + areaB - inter;
            if (union <= 0.0)
            {
                return 0.0;
            }

            return inter / union;
        }

        private static double CenterDistance(double[] a, double[] b)
        {
            var acx = (a[0] + a[2]) / 2.0;
            var acy = (a[1] + a[3]) / 2.0;
            var bcx = (b[0] + b[2]) / 2.0;
            var bcy = (b[1] + b[3]) / 2.0;
            var dx = acx - bcx;
            var dy = acy - bcy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double[] NormalizeBbox(double x0, double y0, double x1, double y1)
        {
            var nx0 = Math.Min(x0, x1);
            var ny0 = Math.Min(y0, y1);
            var nx1 = Math.Max(x0, x1);
            var ny1 = Math.Max(y0, y1);
            return new[] { nx0, ny0, nx1, ny1 };
        }

        private static double Clamp01(double v)
        {
            if (v < 0.0)
            {
                return 0.0;
            }

            if (v > 1.0)
            {
                return 1.0;
            }

            return v;
        }

        private static double[]? TryReadBbox(JArray? arr)
        {
            if (arr == null || arr.Count != 4)
            {
                return null;
            }

            var result = new double[4];
            for (var i = 0; i < 4; i++)
            {
                var v = arr[i]?.Value<double?>();
                if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value))
                {
                    return null;
                }

                result[i] = v.Value;
            }

            return result;
        }

        private static (double X, double Y)? TryReadPoint(JObject? obj)
        {
            if (obj == null)
            {
                return null;
            }

            var x = obj["x"]?.Value<double?>();
            var y = obj["y"]?.Value<double?>();
            if (!x.HasValue || !y.HasValue)
            {
                return null;
            }

            return (x.Value, y.Value);
        }

        private static JArray ToJArray(IReadOnlyList<double> bbox)
        {
            return new JArray(
                Math.Round(bbox[0], 4),
                Math.Round(bbox[1], 4),
                Math.Round(bbox[2], 4),
                Math.Round(bbox[3], 4));
        }

        private static JObject LoadJsonObject(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException(label + " não encontrado.", path);
            }

            var content = File.ReadAllText(path, Encoding.UTF8);
            try
            {
                return JObject.Parse(content);
            }
            catch (JsonReaderException ex)
            {
                throw new InvalidOperationException(label + " inválido (JSON malformado): " + path, ex);
            }
        }

        private sealed class TileInfo
        {
            public TileInfo(string tileId, double[] bboxPt, double widthPx, double heightPx)
            {
                TileId = tileId;
                BboxPt = bboxPt;
                WidthPx = widthPx;
                HeightPx = heightPx;
            }

            public string TileId { get; }
            public double[] BboxPt { get; }
            public double WidthPx { get; }
            public double HeightPx { get; }
        }

        private sealed class GeometryCandidate
        {
            public GeometryCandidate(string kind, int sourceIndex, double[] bboxPt, double score = 0.0)
            {
                Kind = kind;
                SourceIndex = sourceIndex;
                BboxPt = bboxPt;
                Score = score;
            }

            public string Kind { get; }
            public int SourceIndex { get; }
            public double[] BboxPt { get; }
            public double Score { get; }
        }
    }

    public sealed class SemanticGeometryMatchResult
    {
        public int TotalDetections { get; set; }
        public int MatchedDetections { get; set; }
        public int UnmatchedDetections { get; set; }
    }
}
