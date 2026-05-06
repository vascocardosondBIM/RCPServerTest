using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Computes quality/observability metrics for semantic pipeline outputs.
    /// </summary>
    public static class SemanticQualityMetricsService
    {
        public static SemanticQualityMetricsResult ComputeAndPersist(
            string semanticPixelsPath,
            string realWorldPath,
            string cleanJsonPath,
            SemanticCalibrationOptions calibrationOptions)
        {
            var semanticRoot = LoadJsonObject(semanticPixelsPath, "semantic_pixels");
            var realRoot = LoadJsonObject(realWorldPath, "semantic_real_world");
            var cleanRoot = LoadJsonObject(cleanJsonPath, "clean");

            if (semanticRoot["detections"] is not JArray detections)
            {
                throw new InvalidOperationException("semantic_pixels sem detections.");
            }

            var total = detections.Count;
            var matched = semanticRoot["matching"]?["matched_count"]?.Value<int?>() ?? 0;
            var unmatched = semanticRoot["matching"]?["unmatched_count"]?.Value<int?>() ?? Math.Max(0, total - matched);
            if (matched + unmatched > total)
            {
                unmatched = Math.Max(0, total - matched);
            }

            var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in detections)
            {
                if (token is JObject obj)
                {
                    var t = obj["type"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        byType[t] = byType.TryGetValue(t, out var current) ? current + 1 : 1;
                    }
                }
            }

            var precision = total > 0 ? (double)matched / total : 0.0;
            var unmatchedRate = total > 0 ? (double)unmatched / total : 0.0;

            var calibration = realRoot["calibration"] as JObject;
            var metersPerPoint = calibration?["meters_per_point"]?.Value<double?>() ?? 0.0;
            var usedScale = calibration?["scale_denominator"]?.Value<int?>();
            var detectedScale = TryDetectScaleDenominator(cleanRoot);
            var calibrationError = ComputeCalibrationError(
                calibrationOptions,
                metersPerPoint,
                usedScale,
                detectedScale);

            var metricsRoot = new JObject
            {
                ["generated_at_utc"] = DateTime.UtcNow.ToString("o"),
                ["source"] = new JObject
                {
                    ["semantic_pixels_path"] = semanticPixelsPath,
                    ["semantic_real_world_path"] = realWorldPath,
                    ["clean_json_path"] = cleanJsonPath
                },
                ["quality"] = new JObject
                {
                    ["match_precision"] = Math.Round(precision, 6),
                    ["unmatched_rate"] = Math.Round(unmatchedRate, 6),
                    ["total_detections"] = total,
                    ["matched_detections"] = matched,
                    ["unmatched_detections"] = unmatched,
                    ["counts_by_type"] = JObject.FromObject(byType)
                },
                ["calibration_error"] = new JObject
                {
                    ["method"] = calibration?["method"]?.ToString() ?? string.Empty,
                    ["absolute_error_m"] = calibrationError.AbsoluteErrorM.HasValue
                        ? Math.Round(calibrationError.AbsoluteErrorM.Value, 6)
                        : JValue.CreateNull(),
                    ["error_percent"] = calibrationError.ErrorPercent.HasValue
                        ? Math.Round(calibrationError.ErrorPercent.Value, 6)
                        : JValue.CreateNull(),
                    ["note"] = calibrationError.Note
                }
            };

            var outputPath = BuildMetricsOutputPath(semanticPixelsPath);
            File.WriteAllText(outputPath, metricsRoot.ToString(Formatting.Indented), new UTF8Encoding(false));

            return new SemanticQualityMetricsResult
            {
                OutputPath = outputPath,
                MatchPrecision = precision,
                UnmatchedRate = unmatchedRate,
                CalibrationErrorPercent = calibrationError.ErrorPercent
            };
        }

        private static CalibrationErrorDiagnostics ComputeCalibrationError(
            SemanticCalibrationOptions options,
            double metersPerPoint,
            int? usedScale,
            int? detectedScale)
        {
            if (metersPerPoint <= 0)
            {
                return new CalibrationErrorDiagnostics(null, null, "meters_per_point inválido.");
            }

            if (string.Equals(options.Mode, "ReferencePoints", StringComparison.OrdinalIgnoreCase))
            {
                if (options.ReferenceDistanceMeters <= 0)
                {
                    return new CalibrationErrorDiagnostics(null, null, "Sem distance_m para avaliar referência.");
                }

                var dx = options.ReferenceP2XPt - options.ReferenceP1XPt;
                var dy = options.ReferenceP2YPt - options.ReferenceP1YPt;
                var refDistancePt = Math.Sqrt(dx * dx + dy * dy);
                if (refDistancePt <= 0)
                {
                    return new CalibrationErrorDiagnostics(null, null, "Pontos de referência inválidos.");
                }

                var estimatedDistanceM = refDistancePt * metersPerPoint;
                var absError = Math.Abs(estimatedDistanceM - options.ReferenceDistanceMeters);
                var pct = options.ReferenceDistanceMeters > 0
                    ? (absError / options.ReferenceDistanceMeters) * 100.0
                    : (double?)null;
                return new CalibrationErrorDiagnostics(absError, pct, "Erro calculado vs. distância de referência.");
            }

            if (detectedScale.HasValue && detectedScale.Value > 0 && usedScale.HasValue && usedScale.Value > 0)
            {
                var absDiff = Math.Abs(usedScale.Value - detectedScale.Value);
                var pct = (absDiff / detectedScale.Value) * 100.0;
                return new CalibrationErrorDiagnostics(absDiff, pct, "Erro percentual entre escala usada e escala detectada.");
            }

            return new CalibrationErrorDiagnostics(null, null, "Sem referência independente para estimar erro de calibração.");
        }

        private static int? TryDetectScaleDenominator(JObject cleanRoot)
        {
            if (cleanRoot["page"]?["text_words"] is not JArray words || words.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var token in words)
            {
                if (token is JObject obj)
                {
                    var text = obj["text"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.Append(text);
                        sb.Append(' ');
                    }
                }
            }

            var content = sb.ToString();
            var match = Regex.Match(content, @"\b1\s*[:/]\s*(\d{2,4})\b");
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale) && scale > 0)
            {
                return scale;
            }

            return null;
        }

        private static JObject LoadJsonObject(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException(label + " não encontrado.", path);
            }

            try
            {
                return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (JsonReaderException ex)
            {
                throw new InvalidOperationException(label + " inválido (JSON malformado): " + path, ex);
            }
        }

        private static string BuildMetricsOutputPath(string semanticPixelsPath)
        {
            var dir = Path.GetDirectoryName(semanticPixelsPath) ?? string.Empty;
            var file = Path.GetFileNameWithoutExtension(semanticPixelsPath);
            if (file.EndsWith("_semantic_pixels", StringComparison.OrdinalIgnoreCase))
            {
                file = file.Substring(0, file.Length - "_semantic_pixels".Length);
            }

            return Path.Combine(dir, file + "_semantic_metrics.json");
        }

        private sealed class CalibrationErrorDiagnostics
        {
            public CalibrationErrorDiagnostics(double? absoluteErrorM, double? errorPercent, string note)
            {
                AbsoluteErrorM = absoluteErrorM;
                ErrorPercent = errorPercent;
                Note = note;
            }

            public double? AbsoluteErrorM { get; }
            public double? ErrorPercent { get; }
            public string Note { get; }
        }
    }

    public sealed class SemanticQualityMetricsResult
    {
        public string OutputPath { get; set; } = string.Empty;
        public double MatchPrecision { get; set; }
        public double UnmatchedRate { get; set; }
        public double? CalibrationErrorPercent { get; set; }
    }
}
