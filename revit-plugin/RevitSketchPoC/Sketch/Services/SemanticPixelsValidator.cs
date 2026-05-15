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
    /// Validates semantic detections emitted by LLMs against semantic_pixels.v1 contract
    /// and tile metadata from semantic_ready_manifest.json.
    /// </summary>
    public static class SemanticPixelsValidator
    {
        private const string SchemaVersionV1 = "semantic_pixels.v1";
        private const string SchemaVersionV2 = "semantic_pixels.v2";

        public static SemanticPixelsValidationResult ValidateTemplate(string semanticPixelsPath, string semanticReadyManifestPath)
        {
            var root = LoadJsonObject(semanticPixelsPath, "semantic_pixels");
            var manifest = LoadJsonObject(semanticReadyManifestPath, "semantic_ready_manifest");
            return ValidateDocument(root, manifest, expectedPage: null);
        }

        public static SemanticPixelsValidationResult ValidateAndPersistFromLlm(
            string llmResponseText,
            string semanticPixelsPath,
            string semanticReadyManifestPath,
            int? expectedPage = null)
        {
            if (string.IsNullOrWhiteSpace(llmResponseText))
            {
                throw new InvalidOperationException("LLM returned empty semantic detections payload.");
            }

            var root = LoadJsonObject(semanticPixelsPath, "semantic_pixels");
            var manifest = LoadJsonObject(semanticReadyManifestPath, "semantic_ready_manifest");
            var detections = ParseDetectionsFromLlmText(llmResponseText);

            root["detections"] = detections;
            var result = ValidateDocument(root, manifest, expectedPage);
            File.WriteAllText(semanticPixelsPath, root.ToString(Formatting.Indented), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return result;
        }

        private static SemanticPixelsValidationResult ValidateDocument(JObject semanticRoot, JObject manifestRoot, int? expectedPage)
        {
            var schema = semanticRoot["schema"]?.ToString();
            if (!string.Equals(schema, SchemaVersionV1, StringComparison.Ordinal) &&
                !string.Equals(schema, SchemaVersionV2, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "semantic_pixels schema inválido. Esperado \"" + SchemaVersionV1 + "\" ou \"" +
                    SchemaVersionV2 + "\" e recebido \"" + (schema ?? "<null>") + "\".");
            }

            var detectionsToken = semanticRoot["detections"];
            if (detectionsToken is not JArray detections)
            {
                throw new InvalidOperationException("semantic_pixels deve conter um array \"detections\".");
            }

            var manifestTiles = ReadTilesById(manifestRoot);
            var semanticPage = semanticRoot["page"]?.Value<int?>() ?? expectedPage;

            var perType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var pages = new HashSet<int>();
            var tileIds = new HashSet<string>(StringComparer.Ordinal);
            var total = 0;

            for (var i = 0; i < detections.Count; i++)
            {
                if (detections[i] is not JObject detection)
                {
                    throw new InvalidOperationException("detections[" + i + "] deve ser um objeto JSON.");
                }

                var type = RequireNonEmptyString(detection, "type", i);
                var confidence = RequireConfidence(detection, i);
                var bbox = RequireBbox(detection, i);
                var page = RequirePositiveInt(detection, "page", i);
                var tileId = RequireNonEmptyString(detection, "tile_id", i);

                if (expectedPage.HasValue && page != expectedPage.Value)
                {
                    throw new InvalidOperationException(
                        "detections[" + i + "].page inválido. Esperado " + expectedPage.Value + " e recebido " + page + ".");
                }

                if (semanticPage.HasValue && page != semanticPage.Value)
                {
                    throw new InvalidOperationException(
                        "detections[" + i + "].page não coincide com semantic_pixels.page (" + semanticPage.Value + ").");
                }

                if (!manifestTiles.TryGetValue(tileId, out var tileMeta))
                {
                    throw new InvalidOperationException(
                        "detections[" + i + "].tile_id inválido: \"" + tileId + "\" não existe no manifest.");
                }

                ValidateBboxWithinTileBounds(bbox, tileMeta, i);

                detection["type"] = type;
                detection["confidence"] = Math.Round(confidence, 6);
                detection["bbox"] = new JArray(
                    Math.Round(bbox[0], 4),
                    Math.Round(bbox[1], 4),
                    Math.Round(bbox[2], 4),
                    Math.Round(bbox[3], 4));
                detection["page"] = page;
                detection["tile_id"] = tileId;

                perType[type] = perType.TryGetValue(type, out var current) ? current + 1 : 1;
                pages.Add(page);
                tileIds.Add(tileId);
                total++;
            }

            return new SemanticPixelsValidationResult
            {
                Schema = schema ?? SchemaVersionV1,
                TotalDetections = total,
                UniqueTiles = tileIds.Count,
                UniquePages = pages.Count,
                CountsByType = perType
            };
        }

        private static Dictionary<string, TileMeta> ReadTilesById(JObject manifestRoot)
        {
            if (manifestRoot["tiles"] is not JArray tiles || tiles.Count == 0)
            {
                throw new InvalidOperationException("semantic_ready_manifest deve conter array \"tiles\" não vazio.");
            }

            var map = new Dictionary<string, TileMeta>(StringComparer.Ordinal);
            for (var i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] is not JObject tile)
                {
                    throw new InvalidOperationException("manifest.tiles[" + i + "] deve ser um objeto.");
                }

                var id = tile["tile_id"]?.ToString();
                var width = tile["image_width_px"]?.Value<int?>() ?? 0;
                var height = tile["image_height_px"]?.Value<int?>() ?? 0;
                if (string.IsNullOrWhiteSpace(id) || width <= 0 || height <= 0)
                {
                    throw new InvalidOperationException(
                        "manifest.tiles[" + i + "] inválido: precisa de tile_id e dimensões image_width_px/image_height_px > 0.");
                }

                map[id] = new TileMeta(width, height);
            }

            return map;
        }

        private static void ValidateBboxWithinTileBounds(IReadOnlyList<double> bbox, TileMeta tile, int index)
        {
            var x0 = bbox[0];
            var y0 = bbox[1];
            var x1 = bbox[2];
            var y1 = bbox[3];

            if (x0 < 0 || y0 < 0 || x1 > tile.WidthPx || y1 > tile.HeightPx)
            {
                throw new InvalidOperationException(
                    "detections[" + index + "].bbox está fora dos limites do tile. " +
                    "bbox=[" + x0.ToString(CultureInfo.InvariantCulture) + "," + y0.ToString(CultureInfo.InvariantCulture) + "," +
                    x1.ToString(CultureInfo.InvariantCulture) + "," + y1.ToString(CultureInfo.InvariantCulture) + "], " +
                    "tile_size_px=[" + tile.WidthPx + "," + tile.HeightPx + "].");
            }
        }

        private static JArray ParseDetectionsFromLlmText(string llmResponseText)
        {
            var cleaned = llmResponseText.Trim()
                .Replace("```json", string.Empty)
                .Replace("```", string.Empty)
                .Trim();

            JToken token;
            try
            {
                token = JToken.Parse(cleaned);
            }
            catch (JsonReaderException ex)
            {
                throw new InvalidOperationException("LLM detections payload is not valid JSON.", ex);
            }

            if (token is JArray detectionsArray)
            {
                return detectionsArray;
            }

            if (token is JObject root && root["detections"] is JArray wrappedDetections)
            {
                return wrappedDetections;
            }

            throw new InvalidOperationException(
                "LLM detections payload deve ser um array JSON ou objeto com campo \"detections\".");
        }

        private static JObject LoadJsonObject(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException(label + " não encontrado.", path);
            }

            var text = File.ReadAllText(path);
            try
            {
                return JObject.Parse(text);
            }
            catch (JsonReaderException ex)
            {
                throw new InvalidOperationException(label + " inválido (JSON malformado): " + path, ex);
            }
        }

        private static string RequireNonEmptyString(JObject node, string propertyName, int index)
        {
            var value = node[propertyName]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("detections[" + index + "]." + propertyName + " é obrigatório.");
            }

            return value;
        }

        private static int RequirePositiveInt(JObject node, string propertyName, int index)
        {
            var value = node[propertyName]?.Value<int?>();
            if (!value.HasValue || value.Value <= 0)
            {
                throw new InvalidOperationException("detections[" + index + "]." + propertyName + " deve ser inteiro > 0.");
            }

            return value.Value;
        }

        private static double RequireConfidence(JObject node, int index)
        {
            var value = node["confidence"]?.Value<double?>();
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 0 || value.Value > 1)
            {
                throw new InvalidOperationException("detections[" + index + "].confidence deve estar em [0.0, 1.0].");
            }

            return value.Value;
        }

        private static IReadOnlyList<double> RequireBbox(JObject node, int index)
        {
            if (node["bbox"] is not JArray bbox || bbox.Count != 4)
            {
                throw new InvalidOperationException("detections[" + index + "].bbox deve ter 4 números [x0,y0,x1,y1].");
            }

            var values = bbox.Select((t, j) =>
            {
                var v = t.Value<double?>();
                if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value))
                {
                    throw new InvalidOperationException(
                        "detections[" + index + "].bbox[" + j + "] deve ser número válido.");
                }

                return v.Value;
            }).ToArray();

            if (values[2] <= values[0] || values[3] <= values[1])
            {
                throw new InvalidOperationException(
                    "detections[" + index + "].bbox inválido: precisa de x1>x0 e y1>y0.");
            }

            return values;
        }

        private readonly struct TileMeta
        {
            public TileMeta(int widthPx, int heightPx)
            {
                WidthPx = widthPx;
                HeightPx = heightPx;
            }

            public int WidthPx { get; }
            public int HeightPx { get; }
        }
    }

    public sealed class SemanticPixelsValidationResult
    {
        public string Schema { get; set; } = "semantic_pixels.v1";
        public int TotalDetections { get; set; }
        public int UniqueTiles { get; set; }
        public int UniquePages { get; set; }
        public Dictionary<string, int> CountsByType { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }
}
