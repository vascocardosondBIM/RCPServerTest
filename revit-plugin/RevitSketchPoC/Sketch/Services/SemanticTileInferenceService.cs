using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core;
using RevitSketchPoC.Core.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Spike 2 semantic stage: runs vision LLM on each tile image and persists validated detections.
    /// </summary>
    public static class SemanticTileInferenceService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

        public static async Task<SemanticTileInferenceResult> RunAsync(
            PluginSettings settings,
            string cleanJsonPath,
            string semanticReadyManifestPath,
            string semanticPixelsPath,
            double maxSnapDistancePt = 6.0,
            SemanticCalibrationOptions? calibrationOptions = null)
        {
            if (string.IsNullOrWhiteSpace(cleanJsonPath) || !File.Exists(cleanJsonPath))
            {
                throw new FileNotFoundException("clean.json não encontrado.", cleanJsonPath);
            }

            if (string.IsNullOrWhiteSpace(semanticReadyManifestPath) || !File.Exists(semanticReadyManifestPath))
            {
                throw new FileNotFoundException("semantic_ready_manifest.json não encontrado.", semanticReadyManifestPath);
            }

            if (string.IsNullOrWhiteSpace(semanticPixelsPath) || !File.Exists(semanticPixelsPath))
            {
                throw new FileNotFoundException("semantic_pixels.json não encontrado.", semanticPixelsPath);
            }

            var manifestRoot = JObject.Parse(File.ReadAllText(semanticReadyManifestPath, Encoding.UTF8));
            var page = manifestRoot["selected_page_number"]?.Value<int?>() ?? 1;
            if (manifestRoot["tiles"] is not JArray tiles || tiles.Count == 0)
            {
                throw new InvalidOperationException("Manifest sem tiles para inferência.");
            }

            var allDetections = new JArray();
            var tilesProcessed = 0;

            foreach (var token in tiles)
            {
                if (token is not JObject tile)
                {
                    continue;
                }

                var tileId = tile["tile_id"]?.ToString();
                var imagePath = tile["image_path"]?.ToString();
                if (string.IsNullOrWhiteSpace(tileId) || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                {
                    continue;
                }

                var assistantText = await InferTileWithProviderAsync(settings, imagePath, tileId, page).ConfigureAwait(false);
                var detections = ParseDetections(assistantText);
                foreach (var d in detections)
                {
                    if (d is not JObject obj)
                    {
                        continue;
                    }

                    obj["tile_id"] = tileId;
                    obj["page"] = page;
                    allDetections.Add(obj);
                }

                tilesProcessed++;
            }

            var aggregatePayload = new JObject
            {
                ["detections"] = allDetections
            }.ToString(Formatting.None);

            var validation = SemanticPixelsValidator.ValidateAndPersistFromLlm(
                aggregatePayload,
                semanticPixelsPath,
                semanticReadyManifestPath,
                expectedPage: page);

            var matching = SemanticGeometryMatcher.MatchAndPersist(
                semanticPixelsPath,
                cleanJsonPath,
                semanticReadyManifestPath,
                maxSnapDistancePt);

            var calibration = SemanticCalibrationService.CalibrateAndExport(
                semanticPixelsPath,
                cleanJsonPath,
                calibrationOptions ?? new SemanticCalibrationOptions());

            var metrics = SemanticQualityMetricsService.ComputeAndPersist(
                semanticPixelsPath,
                calibration.OutputPath,
                cleanJsonPath,
                calibrationOptions ?? new SemanticCalibrationOptions());

            return new SemanticTileInferenceResult
            {
                Page = page,
                TilesProcessed = tilesProcessed,
                TotalDetections = validation.TotalDetections,
                MatchedDetections = matching.MatchedDetections,
                UnmatchedDetections = matching.UnmatchedDetections,
                CalibrationMethod = calibration.Method,
                RealWorldOutputPath = calibration.OutputPath,
                MetricsOutputPath = metrics.OutputPath,
                MatchPrecision = metrics.MatchPrecision,
                UnmatchedRate = metrics.UnmatchedRate,
                CalibrationErrorPercent = metrics.CalibrationErrorPercent
            };
        }

        private static async Task<string> InferTileWithProviderAsync(
            PluginSettings settings,
            string imagePath,
            string tileId,
            int page)
        {
            var provider = string.IsNullOrWhiteSpace(settings.LlmProvider)
                ? "Ollama"
                : settings.LlmProvider.Trim();

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return await InferWithOllamaAsync(settings, imagePath, tileId, page).ConfigureAwait(false);
            }

            if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return await InferWithGeminiAsync(settings, imagePath, tileId, page).ConfigureAwait(false);
            }

            if (string.Equals(provider, "Nvidia", StringComparison.OrdinalIgnoreCase))
            {
                return await InferWithNvidiaAsync(settings, imagePath, tileId, page).ConfigureAwait(false);
            }

            throw new InvalidOperationException("LlmProvider não suportado para Spike 2: " + provider);
        }

        private static async Task<string> InferWithOllamaAsync(
            PluginSettings settings,
            string imagePath,
            string tileId,
            int page)
        {
            var model = string.IsNullOrWhiteSpace(settings.OllamaModel) ? "llava" : settings.OllamaModel.Trim();
            var baseUrl = NormalizeOllamaBaseUrl(settings.OllamaBaseUrl);
            var url = baseUrl.TrimEnd('/') + "/api/chat";

            var body = new
            {
                model,
                stream = false,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = BuildTilePrompt(tileId, page),
                        images = new[] { Convert.ToBase64String(File.ReadAllBytes(imagePath)) }
                    }
                }
            };

            var payload = await PostJsonAsync(url, body, headers: null).ConfigureAwait(false);
            var root = JObject.Parse(payload);
            var text = root["message"]?["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Ollama retornou conteúdo vazio para tile " + tileId + ".");
            }

            return text.Trim();
        }

        private static async Task<string> InferWithGeminiAsync(
            PluginSettings settings,
            string imagePath,
            string tileId,
            int page)
        {
            if (string.IsNullOrWhiteSpace(settings.GeminiApiKey))
            {
                throw new InvalidOperationException("GeminiApiKey vazio para inferência Spike 2.");
            }

            var model = NormalizeGeminiModelId(settings.GeminiModel);
            var url =
                "https://generativelanguage.googleapis.com/v1beta/models/" +
                model +
                ":generateContent?key=" +
                Uri.EscapeDataString(settings.GeminiApiKey);

            var bytes = File.ReadAllBytes(imagePath);
            var mimeType = ResolveImageMimeType(imagePath);
            var body = new
            {
                contents = new object[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = BuildTilePrompt(tileId, page) },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = mimeType,
                                    data = Convert.ToBase64String(bytes)
                                }
                            }
                        }
                    }
                }
            };

            var payload = await PostJsonAsync(url, body, headers: null).ConfigureAwait(false);
            return SketchInterpretationParser.ExtractAssistantTextFromGeminiResponse(payload);
        }

        private static async Task<string> InferWithNvidiaAsync(
            PluginSettings settings,
            string imagePath,
            string tileId,
            int page)
        {
            if (string.IsNullOrWhiteSpace(settings.NvidiaApiKey))
            {
                throw new InvalidOperationException("NvidiaApiKey vazio para inferência Spike 2.");
            }

            var model = string.IsNullOrWhiteSpace(settings.NvidiaModel)
                ? "google/gemma-3n-e4b-it"
                : settings.NvidiaModel.Trim();
            var url = NormalizeNvidiaUrl(settings.NvidiaChatCompletionsUrl);
            var bytes = File.ReadAllBytes(imagePath);
            var mimeType = ResolveImageMimeType(imagePath);
            var dataUrl = "data:" + mimeType + ";base64," + Convert.ToBase64String(bytes);

            var body = new
            {
                model,
                stream = false,
                max_tokens = 2048,
                temperature = 0.1,
                top_p = 0.7,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = BuildTilePrompt(tileId, page) },
                            new { type = "image_url", image_url = new { url = dataUrl } }
                        }
                    }
                }
            };

            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + settings.NvidiaApiKey.Trim() },
                { "Accept", "application/json" }
            };

            var payload = await PostJsonAsync(url, body, headers).ConfigureAwait(false);
            return OpenAiChatCompletionParser.ExtractAssistantContent(payload);
        }

        private static async Task<string> PostJsonAsync(string url, object body, Dictionary<string, string>? headers)
        {
            var json = JsonConvert.SerializeObject(body);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                if (headers != null)
                {
                    foreach (var kv in headers)
                    {
                        if (string.Equals(kv.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                        {
                            request.Headers.Authorization = AuthenticationHeaderValue.Parse(kv.Value);
                            continue;
                        }

                        request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                }

                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await Http.SendAsync(request).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        "Falha na chamada do provider (" + (int)response.StatusCode + "). URL: " + url + "\n\n" + payload);
                }

                return payload;
            }
        }

        private static JArray ParseDetections(string assistantText)
        {
            var cleaned = assistantText
                .Replace("```json", string.Empty)
                .Replace("```", string.Empty)
                .Trim();

            JToken token;
            try
            {
                token = JToken.Parse(cleaned);
            }
            catch
            {
                var firstArray = cleaned.IndexOf('[');
                var lastArray = cleaned.LastIndexOf(']');
                if (firstArray >= 0 && lastArray > firstArray)
                {
                    var arraySlice = cleaned.Substring(firstArray, lastArray - firstArray + 1);
                    token = JToken.Parse(arraySlice);
                }
                else
                {
                    throw new InvalidOperationException("Resposta semântica do LLM não está em JSON válido.");
                }
            }

            if (token is JArray arr)
            {
                return arr;
            }

            if (token is JObject obj && obj["detections"] is JArray wrapped)
            {
                return wrapped;
            }

            throw new InvalidOperationException("Resposta semântica do LLM deve ser array ou {\"detections\": [...]}.");
        }

        private static string BuildTilePrompt(string tileId, int page)
        {
            return
                "You are a floor-plan semantic detector for BIM.\n" +
                "Analyze ONLY this tile image and return detections for visible elements.\n" +
                "Return ONLY valid JSON (no markdown).\n" +
                "Output format must be either an array or {\"detections\": [...] }.\n" +
                "Each detection MUST contain: type, confidence, bbox, page, tile_id.\n" +
                "Rules:\n" +
                "- bbox format: [x0,y0,x1,y1] in pixel coordinates RELATIVE to this tile image.\n" +
                "- confidence in [0,1].\n" +
                "- page must be " + page + ".\n" +
                "- tile_id must be \"" + tileId + "\".\n" +
                "- If nothing relevant is present, return an empty detections list.\n" +
                "Typical types: wall, door, window, room, stairs, column, other.";
        }

        private static string NormalizeOllamaBaseUrl(string? url)
        {
            var u = string.IsNullOrWhiteSpace(url) ? "http://localhost:11434" : url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                u = "http://" + u;
            }

            return u;
        }

        private static string NormalizeGeminiModelId(string? model)
        {
            var id = string.IsNullOrWhiteSpace(model) ? "gemini-2.0-flash" : model.Trim();
            if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            {
                id = id.Substring("models/".Length);
            }

            return id;
        }

        private static string NormalizeNvidiaUrl(string? url)
        {
            var u = string.IsNullOrWhiteSpace(url)
                ? "https://integrate.api.nvidia.com/v1/chat/completions"
                : url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                u = "https://" + u;
            }

            return u;
        }

        private static string ResolveImageMimeType(string imagePath)
        {
            var ext = Path.GetExtension(imagePath)?.ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg")
            {
                return "image/jpeg";
            }

            return "image/png";
        }
    }

    public sealed class SemanticTileInferenceResult
    {
        public int Page { get; set; }
        public int TilesProcessed { get; set; }
        public int TotalDetections { get; set; }
        public int MatchedDetections { get; set; }
        public int UnmatchedDetections { get; set; }
        public string CalibrationMethod { get; set; } = string.Empty;
        public string RealWorldOutputPath { get; set; } = string.Empty;
        public string MetricsOutputPath { get; set; } = string.Empty;
        public double MatchPrecision { get; set; }
        public double UnmatchedRate { get; set; }
        public double? CalibrationErrorPercent { get; set; }
    }
}
