using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Converts page-point coordinates into real-world meters using explicit calibration.
    /// </summary>
    public static class SemanticCalibrationService
    {
        public static SemanticCalibrationResult CalibrateAndExport(
            string semanticPixelsPath,
            string cleanJsonPath,
            SemanticCalibrationOptions options)
        {
            if (string.IsNullOrWhiteSpace(semanticPixelsPath) || !File.Exists(semanticPixelsPath))
            {
                throw new FileNotFoundException("semantic_pixels.json não encontrado.", semanticPixelsPath);
            }

            if (string.IsNullOrWhiteSpace(cleanJsonPath) || !File.Exists(cleanJsonPath))
            {
                throw new FileNotFoundException("clean.json não encontrado.", cleanJsonPath);
            }

            var semanticRoot = JObject.Parse(File.ReadAllText(semanticPixelsPath, Encoding.UTF8));
            var cleanRoot = JObject.Parse(File.ReadAllText(cleanJsonPath, Encoding.UTF8));
            var page = cleanRoot["page"] as JObject
                       ?? throw new InvalidOperationException("clean.json sem bloco page.");
            var pageWidthPt = page["width_pt"]?.Value<double?>() ?? 0;
            var pageHeightPt = page["height_pt"]?.Value<double?>() ?? 0;
            if (pageWidthPt <= 0 || pageHeightPt <= 0)
            {
                throw new InvalidOperationException("clean.json com dimensões de página inválidas.");
            }

            var calibration = ResolveCalibration(cleanRoot, options);
            if (semanticRoot["detections"] is not JArray detections)
            {
                throw new InvalidOperationException("semantic_pixels sem array detections.");
            }

            foreach (var token in detections)
            {
                if (token is not JObject detection)
                {
                    continue;
                }

                var bboxPt = ReadBestPointBbox(detection);
                var bboxM = new[]
                {
                    bboxPt[0] * calibration.MetersPerPoint,
                    bboxPt[1] * calibration.MetersPerPoint,
                    bboxPt[2] * calibration.MetersPerPoint,
                    bboxPt[3] * calibration.MetersPerPoint
                };
                var centerM = new[]
                {
                    ((bboxPt[0] + bboxPt[2]) / 2.0) * calibration.MetersPerPoint,
                    ((bboxPt[1] + bboxPt[3]) / 2.0) * calibration.MetersPerPoint
                };

                detection["bbox_real_m"] = new JArray(
                    Math.Round(bboxM[0], 6),
                    Math.Round(bboxM[1], 6),
                    Math.Round(bboxM[2], 6),
                    Math.Round(bboxM[3], 6));
                detection["center_real_m"] = new JArray(
                    Math.Round(centerM[0], 6),
                    Math.Round(centerM[1], 6));
            }

            semanticRoot["calibration"] = new JObject
            {
                ["applied"] = true,
                ["method"] = calibration.Method,
                ["meters_per_point"] = Math.Round(calibration.MetersPerPoint, 10),
                ["scale_denominator"] = calibration.ScaleDenominator.HasValue
                    ? calibration.ScaleDenominator.Value
                    : JValue.CreateNull(),
                ["notes"] = calibration.Notes
            };

            semanticRoot["real_coordinates"] = new JObject
            {
                ["units"] = "meters",
                ["origin"] = "page_bottom_left",
                ["page_width_m"] = Math.Round(pageWidthPt * calibration.MetersPerPoint, 6),
                ["page_height_m"] = Math.Round(pageHeightPt * calibration.MetersPerPoint, 6)
            };

            var outputPath = BuildRealWorldOutputPath(semanticPixelsPath);
            File.WriteAllText(outputPath, semanticRoot.ToString(Formatting.Indented), new UTF8Encoding(false));

            return new SemanticCalibrationResult
            {
                OutputPath = outputPath,
                Method = calibration.Method,
                MetersPerPoint = calibration.MetersPerPoint,
                ScaleDenominator = calibration.ScaleDenominator
            };
        }

        private static SemanticCalibrationResolved ResolveCalibration(JObject cleanRoot, SemanticCalibrationOptions options)
        {
            var mode = string.IsNullOrWhiteSpace(options.Mode) ? "AutoScale" : options.Mode.Trim();
            if (string.Equals(mode, "ReferencePoints", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveFromReferencePoints(options);
            }

            if (string.Equals(mode, "ManualScale", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveFromScale(options.ManualScaleDenominator, "manual_scale");
            }

            var detected = TryDetectScaleDenominator(cleanRoot);
            if (detected.HasValue)
            {
                return ResolveFromScale(detected.Value, "detected_scale");
            }

            if (options.ManualScaleDenominator > 0)
            {
                return ResolveFromScale(options.ManualScaleDenominator, "auto_fallback_manual_scale");
            }

            throw new InvalidOperationException(
                "Não foi possível detetar escala automaticamente. Use ManualScale ou ReferencePoints.");
        }

        private static SemanticCalibrationResolved ResolveFromScale(int denominator, string method)
        {
            if (denominator <= 0)
            {
                throw new InvalidOperationException("Scale denominator deve ser > 0.");
            }

            // 1 pt = 1/72 inch; 1 inch = 0.0254 m; real_m = paper_m * scale.
            var metersPerPoint = denominator * (0.0254 / 72.0);
            return new SemanticCalibrationResolved
            {
                Method = method,
                MetersPerPoint = metersPerPoint,
                ScaleDenominator = denominator,
                Notes = "Calibrado por escala 1:" + denominator
            };
        }

        private static SemanticCalibrationResolved ResolveFromReferencePoints(SemanticCalibrationOptions options)
        {
            if (options.ReferenceDistanceMeters <= 0)
            {
                throw new InvalidOperationException("ReferenceDistanceMeters deve ser > 0.");
            }

            var dx = options.ReferenceP2XPt - options.ReferenceP1XPt;
            var dy = options.ReferenceP2YPt - options.ReferenceP1YPt;
            var distancePt = Math.Sqrt(dx * dx + dy * dy);
            if (distancePt <= 0)
            {
                throw new InvalidOperationException("Pontos de referência inválidos: distância em pt deve ser > 0.");
            }

            var metersPerPoint = options.ReferenceDistanceMeters / distancePt;
            return new SemanticCalibrationResolved
            {
                Method = "reference_points",
                MetersPerPoint = metersPerPoint,
                ScaleDenominator = null,
                Notes =
                    "Calibrado por pontos de referência. p1=(" + options.ReferenceP1XPt.ToString(CultureInfo.InvariantCulture) +
                    "," + options.ReferenceP1YPt.ToString(CultureInfo.InvariantCulture) + "), p2=(" +
                    options.ReferenceP2XPt.ToString(CultureInfo.InvariantCulture) + "," +
                    options.ReferenceP2YPt.ToString(CultureInfo.InvariantCulture) +
                    "), distance_m=" + options.ReferenceDistanceMeters.ToString(CultureInfo.InvariantCulture)
            };
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
                    var t = obj["text"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        sb.Append(t.Trim());
                        sb.Append(' ');
                    }
                }
            }

            var text = sb.ToString();
            var directMatch = Regex.Match(text, @"\b1\s*[:/]\s*(\d{2,4})\b");
            if (directMatch.Success && int.TryParse(directMatch.Groups[1].Value, out var scale) && scale > 0)
            {
                return scale;
            }

            return null;
        }

        private static double[] ReadBestPointBbox(JObject detection)
        {
            if (detection["bbox_snapped_pt"] is JArray snapped && TryReadBbox(snapped, out var snappedVals))
            {
                return snappedVals;
            }

            if (detection["bbox_original_pt"] is JArray original && TryReadBbox(original, out var originalVals))
            {
                return originalVals;
            }

            if (detection["bbox_pt"] is JArray pt && TryReadBbox(pt, out var ptVals))
            {
                return ptVals;
            }

            throw new InvalidOperationException("Detection sem bbox em coordenada de página (pt).");
        }

        private static bool TryReadBbox(JArray bbox, out double[] values)
        {
            values = new double[4];
            if (bbox.Count != 4)
            {
                return false;
            }

            for (var i = 0; i < 4; i++)
            {
                var v = bbox[i]?.Value<double?>();
                if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value))
                {
                    return false;
                }

                values[i] = v.Value;
            }

            return true;
        }

        private static string BuildRealWorldOutputPath(string semanticPixelsPath)
        {
            var dir = Path.GetDirectoryName(semanticPixelsPath) ?? string.Empty;
            var file = Path.GetFileNameWithoutExtension(semanticPixelsPath);
            if (file.EndsWith("_semantic_pixels", StringComparison.OrdinalIgnoreCase))
            {
                file = file.Substring(0, file.Length - "_semantic_pixels".Length);
            }

            return Path.Combine(dir, file + "_semantic_real_world.json");
        }

        private sealed class SemanticCalibrationResolved
        {
            public string Method { get; set; } = "detected_scale";
            public double MetersPerPoint { get; set; }
            public int? ScaleDenominator { get; set; }
            public string Notes { get; set; } = string.Empty;
        }
    }

    public sealed class SemanticCalibrationOptions
    {
        public string Mode { get; set; } = "AutoScale";
        public int ManualScaleDenominator { get; set; } = 100;
        public double ReferenceP1XPt { get; set; }
        public double ReferenceP1YPt { get; set; }
        public double ReferenceP2XPt { get; set; }
        public double ReferenceP2YPt { get; set; }
        public double ReferenceDistanceMeters { get; set; }
    }

    public sealed class SemanticCalibrationResult
    {
        public string OutputPath { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public double MetersPerPoint { get; set; }
        public int? ScaleDenominator { get; set; }
    }
}
