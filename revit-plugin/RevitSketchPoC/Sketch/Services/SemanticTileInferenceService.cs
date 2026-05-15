using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Sketch.Contracts;
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
        private const int ProviderRetryAttempts = 3;
        public static IReadOnlyList<string> SemanticStepOrder { get; } = new[]
        {
            JobStepNames.Walls,
            JobStepNames.Openings,
            JobStepNames.Rooms,
            JobStepNames.FloorsCeilings,
            JobStepNames.FixturesFurniture
        };

        public static async Task<SemanticTileInferenceResult> RunAsync(
            PluginSettings settings,
            string cleanJsonPath,
            string semanticReadyManifestPath,
            string semanticPixelsPath,
            double maxSnapDistancePt = 6.0,
            SemanticCalibrationOptions? calibrationOptions = null)
        {
            return await RunStepsAsync(
                settings,
                cleanJsonPath,
                semanticReadyManifestPath,
                semanticPixelsPath,
                SemanticStepOrder,
                maxSnapDistancePt,
                calibrationOptions).ConfigureAwait(false);
        }

        public static async Task<SemanticTileInferenceResult> RunStepsAsync(
            PluginSettings settings,
            string cleanJsonPath,
            string semanticReadyManifestPath,
            string semanticPixelsPath,
            IReadOnlyList<string> stepNames,
            double maxSnapDistancePt = 6.0,
            SemanticCalibrationOptions? calibrationOptions = null)
        {
            if (stepNames == null || stepNames.Count == 0)
            {
                throw new InvalidOperationException("Nenhum step semântico fornecido para execução.");
            }

            SemanticTileInferenceResult? last = null;
            foreach (var stepName in stepNames)
            {
                last = await RunStepAsync(
                    settings,
                    cleanJsonPath,
                    semanticReadyManifestPath,
                    semanticPixelsPath,
                    stepName,
                    maxSnapDistancePt,
                    calibrationOptions).ConfigureAwait(false);
            }

            return last ?? new SemanticTileInferenceResult();
        }

        public static async Task<SemanticTileInferenceResult> RunStepAsync(
            PluginSettings settings,
            string cleanJsonPath,
            string semanticReadyManifestPath,
            string semanticPixelsPath,
            string stepName,
            double maxSnapDistancePt = 6.0,
            SemanticCalibrationOptions? calibrationOptions = null,
            Action<SemanticTileStepProgress>? tileProgressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(stepName))
            {
                throw new InvalidOperationException("stepName inválido para execução semântica.");
            }

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
            var totalTiles = 0;
            foreach (var token in tiles)
            {
                if (token is not JObject tile)
                {
                    continue;
                }

                var imagePathCandidate = tile["image_path"]?.ToString();
                if (!string.IsNullOrWhiteSpace(imagePathCandidate) && File.Exists(imagePathCandidate))
                {
                    totalTiles++;
                }
            }

            var semanticRoot = JObject.Parse(File.ReadAllText(semanticPixelsPath, Encoding.UTF8));
            var existingDetections = semanticRoot["detections"] as JArray ?? new JArray();
            var mergedDetections = new JArray();
            foreach (var item in existingDetections)
            {
                mergedDetections.Add(item);
            }

            var tilesProcessed = 0;
            var stepDetections = 0;

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

                tileProgressCallback?.Invoke(new SemanticTileStepProgress
                {
                    StepName = stepName,
                    TileId = tileId,
                    TilesProcessed = tilesProcessed,
                    TotalTiles = totalTiles,
                    Status = "tile_started"
                });

                try
                {
                    var assistantText = await InferTileWithProviderAsync(settings, imagePath, tileId, page, stepName).ConfigureAwait(false);
                    var detections = ParseDetectionsSafe(assistantText);
                    foreach (var d in detections)
                    {
                        if (d is not JObject obj)
                        {
                            continue;
                        }

                        obj["tile_id"] = tileId;
                        obj["page"] = page;
                        obj["step"] = stepName;
                        mergedDetections.Add(obj);
                        stepDetections++;
                    }

                    tilesProcessed++;
                    tileProgressCallback?.Invoke(new SemanticTileStepProgress
                    {
                        StepName = stepName,
                        TileId = tileId,
                        TilesProcessed = tilesProcessed,
                        TotalTiles = totalTiles,
                        Status = "tile_finished"
                    });
                }
                catch
                {
                    // Isolate malformed/provider-specific failures to one tile.
                    // The step keeps running so large plans are not aborted by a single bad response.
                    tilesProcessed++;
                    tileProgressCallback?.Invoke(new SemanticTileStepProgress
                    {
                        StepName = stepName,
                        TileId = tileId,
                        TilesProcessed = tilesProcessed,
                        TotalTiles = totalTiles,
                        Status = "tile_failed"
                    });
                }
            }

            var aggregatePayload = new JObject
            {
                ["detections"] = mergedDetections
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
                StepName = stepName,
                Page = page,
                TilesProcessed = tilesProcessed,
                StepDetections = stepDetections,
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
            int page,
            string stepName)
        {
            var provider = string.IsNullOrWhiteSpace(settings.LlmProvider)
                ? "Ollama"
                : settings.LlmProvider.Trim();

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return await InferWithOllamaAsync(settings, imagePath, tileId, page, stepName).ConfigureAwait(false);
            }

            if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return await InferWithGeminiAsync(settings, imagePath, tileId, page, stepName).ConfigureAwait(false);
            }

            if (string.Equals(provider, "Nvidia", StringComparison.OrdinalIgnoreCase))
            {
                return await InferWithNvidiaAsync(settings, imagePath, tileId, page, stepName).ConfigureAwait(false);
            }

            throw new InvalidOperationException("LlmProvider não suportado para Spike 2: " + provider);
        }

        private static async Task<string> InferWithOllamaAsync(
            PluginSettings settings,
            string imagePath,
            string tileId,
            int page,
            string stepName)
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
                        content = BuildTilePrompt(tileId, page, stepName),
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
            int page,
            string stepName)
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
                            new { text = BuildTilePrompt(tileId, page, stepName) },
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
            int page,
            string stepName)
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
                            new { type = "text", text = BuildTilePrompt(tileId, page, stepName) },
                            new { type = "image_url", image_url = new { url = dataUrl } }
                        }
                    }
                }
            };
            var payloadBody = BuildNvidiaPayloadBody(body, settings, disableReasoning: false);

            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + settings.NvidiaApiKey.Trim() },
                { "Accept", "application/json" }
            };

            try
            {
                var payload = await PostJsonAsync(url, payloadBody, headers).ConfigureAwait(false);
                return OpenAiChatCompletionParser.ExtractAssistantContent(payload);
            }
            catch (InvalidOperationException ex) when (
                ShouldRetryWithoutReasoning(ex, settings))
            {
                var fallbackPayload = BuildNvidiaPayloadBody(body, settings, disableReasoning: true);
                var payload = await PostJsonAsync(url, fallbackPayload, headers).ConfigureAwait(false);
                return OpenAiChatCompletionParser.ExtractAssistantContent(payload);
            }
            catch (InvalidOperationException ex) when (
                model.IndexOf("gpt-oss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                model.IndexOf("gpt", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException(
                    ex.Message +
                    "\n\nPossível causa: o modelo configurado no provider Nvidia pode não suportar entrada de imagem (vision)." +
                    "\nSpike 2 envia tiles como imagem; use um modelo VLM/multimodal no campo NvidiaModel.",
                    ex);
            }
        }

        private static async Task<string> PostJsonAsync(string url, object body, Dictionary<string, string>? headers)
        {
            var json = JsonConvert.SerializeObject(body);
            for (var attempt = 1; attempt <= ProviderRetryAttempts; attempt++)
            {
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
                    if (response.IsSuccessStatusCode)
                    {
                        return payload;
                    }

                    var statusCode = (int)response.StatusCode;
                    var isRetryable =
                        statusCode == 429 ||
                        statusCode == 500 ||
                        statusCode == 502 ||
                        statusCode == 503 ||
                        statusCode == 504;
                    var hasAttemptsLeft = attempt < ProviderRetryAttempts;
                    if (isRetryable && hasAttemptsLeft)
                    {
                        var delayMs = attempt * 2000;
                        await Task.Delay(delayMs).ConfigureAwait(false);
                        continue;
                    }

                    throw new InvalidOperationException(
                        "Falha na chamada do provider (" + statusCode + "). URL: " + url +
                        "\nTentativas: " + attempt + "/" + ProviderRetryAttempts +
                        "\n\n" + payload);
                }
            }

            throw new InvalidOperationException("Falha inesperada ao chamar provider após retries.");
        }

        private static JArray ParseDetectionsSafe(string assistantText)
        {
            try
            {
                return ParseDetections(assistantText);
            }
            catch
            {
                // Keep pipeline resilient to occasional malformed/truncated model payloads.
                // If one tile is malformed, we skip detections from that tile and continue.
                return new JArray();
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
                var balanced = ExtractFirstBalancedJson(cleaned);
                if (!string.IsNullOrWhiteSpace(balanced))
                {
                    token = JToken.Parse(balanced);
                }
                else
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

        private static string? ExtractFirstBalancedJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var start = -1;
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (start < 0)
                {
                    if (c == '{' || c == '[')
                    {
                        start = i;
                        depth = 1;
                    }

                    continue;
                }

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{' || c == '[')
                {
                    depth++;
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        return text.Substring(start, i - start + 1);
                    }
                }
            }

            return null;
        }

        private static string BuildTilePrompt(string tileId, int page, string stepName)
        {
            var stepInstruction = BuildStepInstruction(stepName);
            return
                "You are a floor-plan semantic detector for BIM.\n" +
                "Current semantic step: " + stepName + ".\n" +
                "Analyze ONLY this tile image and return detections for visible elements.\n" +
                "Return ONLY valid JSON (no markdown).\n" +
                "Output format must be either an array or {\"detections\": [...] }.\n" +
                "Each detection MUST contain: type, confidence, bbox, page, tile_id.\n" +
                stepInstruction + "\n" +
                "Rules:\n" +
                "- bbox format: [x0,y0,x1,y1] in pixel coordinates RELATIVE to this tile image.\n" +
                "- confidence in [0,1].\n" +
                "- page must be " + page + ".\n" +
                "- tile_id must be \"" + tileId + "\".\n" +
                "- If nothing relevant is present, return an empty detections list.\n" +
                "Typical types: wall, door, window, room, stairs, column, other.";
        }

        private static string BuildStepInstruction(string stepName)
        {
            if (string.Equals(stepName, JobStepNames.Walls, StringComparison.OrdinalIgnoreCase))
            {
                return "Step target: detect ONLY walls (type=wall).";
            }

            if (string.Equals(stepName, JobStepNames.Openings, StringComparison.OrdinalIgnoreCase))
            {
                return "Step target: detect ONLY doors/windows/openings (type=door|window|opening).";
            }

            if (string.Equals(stepName, JobStepNames.Rooms, StringComparison.OrdinalIgnoreCase))
            {
                return "Step target: detect ONLY rooms/zones (type=room).";
            }

            if (string.Equals(stepName, JobStepNames.FloorsCeilings, StringComparison.OrdinalIgnoreCase))
            {
                return "Step target: detect ONLY floors/ceilings (type=floor|ceiling).";
            }

            if (string.Equals(stepName, JobStepNames.FixturesFurniture, StringComparison.OrdinalIgnoreCase))
            {
                return "Step target: detect ONLY fixtures/furniture (type=toilet|sink|sofa|furniture|fixture).";
            }

            return "Step target: detect relevant elements for this step only.";
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

        private static JObject BuildNvidiaPayloadBody(object body, PluginSettings settings, bool disableReasoning)
        {
            var payloadBody = JObject.FromObject(body);
            var extraBody = BuildNvidiaExtraBody(settings, disableReasoning);
            if (extraBody != null)
            {
                payloadBody["extra_body"] = JObject.FromObject(extraBody);
            }

            return payloadBody;
        }

        private static bool ShouldRetryWithoutReasoning(Exception ex, PluginSettings settings)
        {
            if (!settings.NvidiaEnableThinking && settings.NvidiaReasoningBudget <= 0)
            {
                return false;
            }

            var msg = ex.Message ?? string.Empty;
            return msg.IndexOf("Unexpected message.content shape: Null", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   msg.IndexOf("reasoning_content", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<string, object>? BuildNvidiaExtraBody(PluginSettings settings, bool disableReasoning)
        {
            if (disableReasoning)
            {
                return null;
            }

            var extraBody = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (settings.NvidiaEnableThinking)
            {
                extraBody["chat_template_kwargs"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    { "enable_thinking", true }
                };
            }

            if (settings.NvidiaReasoningBudget > 0)
            {
                extraBody["reasoning_budget"] = settings.NvidiaReasoningBudget;
            }

            return extraBody.Count == 0 ? null : extraBody;
        }
    }

    public sealed class SemanticTileInferenceResult
    {
        public string StepName { get; set; } = string.Empty;
        public int Page { get; set; }
        public int TilesProcessed { get; set; }
        public int StepDetections { get; set; }
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

    public sealed class SemanticTileStepProgress
    {
        public string StepName { get; set; } = string.Empty;
        public string TileId { get; set; } = string.Empty;
        public int TilesProcessed { get; set; }
        public int TotalTiles { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
