using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Chat.Contracts;
using RevitSketchPoC.Sketch.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace RevitSketchPoC.Chat.Services
{
    /// <summary>
    /// Multimodal chat (text + optional images per user turn) and Revit context in the system prompt.
    /// </summary>
    public sealed class LlmChatService
    {
        private const string SystemPromptBase =
            "You are a helpful assistant for Autodesk Revit users (architecture / BIM). " +
            "Answer clearly and concisely in the same language the user writes (Portuguese or English). " +
            "When a JSON block titled Revit context is provided, treat it as read-only facts from the open model: " +
            "do not invent element ids, names, or counts that contradict that data. " +
            "If the user sends an image, describe or use what you see to help with Revit/BIM questions.\n\n" +
            "When the user wants changes applied in Revit, include EXACTLY one fenced JSON code block using this shape (no extra keys at root):\n" +
            "```json\n{ \"revitOps\": [ { \"op\": \"set_parameter\", \"elementId\": 12345, \"parameterName\": \"Comments\", \"value\": \"text\" } ] }\n```\n" +
            "Allowed ops:\n" +
            "- set_parameter: elementId (integer), value (string for SetValueString), and parameterName (localized name as in the context JSON) and/or builtInParameter (e.g. WALL_BASE_CONSTRAINT). For levels/refs use the level name as shown in \"value\" in the context.\n" +
            "- delete_elements: elementIds (array of integers)\n" +
            "- select_elements: elementIds (array of integers)\n" +
            "- create_wall: startX, startY, endX, endY (numbers, metres in project XY, same as sketch upload); optional heightMeters, levelName, wallTypeName (match names from context).\n" +
            "- create_room: centerX, centerY (metres) or center { x, y }; optional name, levelName.\n" +
            "- create_door: locationX, locationY (metres) or location { x, y }; optional hostWallId (integer wall id from context); optional levelName. If hostWallId is omitted, the nearest wall on that level is used.\n" +
            "- change_element_level: default when the user wants to move elements to another level without keeping world position. Same ids/level fields as below; optional preserveWorldPosition (boolean, default false) or preservePosition — set true only when the user explicitly asks to keep the same XYZ in the model.\n" +
            "- change_level_preserve_position: same fields as change_element_level but always preserves world Z (equivalent to preserveWorldPosition true). Use when the user clearly wants height/position unchanged in space.\n" +
            "  Common fields for both: elementIds (array) and/or elementId; targetLevelName or targetLevelId. Supported: FamilyInstance (level-hosted), Wall, Floor, Ceiling.\n" +
            "Use only element ids from the Revit context when possible. Prefer few, safe operations; the user can run the chat again.";

        private readonly PluginSettings _settings;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };

        public LlmChatService(PluginSettings settings)
        {
            _settings = settings;
        }

        /// <param name="revitContextForSystem">Optional Revit JSON/text merged into the system instruction each call.</param>
        public Task<string> CompleteAsync(
            IReadOnlyList<ChatLlmTurn> turns,
            string? revitContextForSystem = null)
        {
            var provider = string.IsNullOrWhiteSpace(_settings.LlmProvider)
                ? "Ollama"
                : _settings.LlmProvider.Trim();

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return CompleteOllamaAsync(turns, revitContextForSystem);
            }

            if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return CompleteGeminiAsync(turns, revitContextForSystem);
            }

            throw new InvalidOperationException(
                "Unknown LlmProvider in pluginsettings.json: \"" + provider + "\". Use \"Ollama\" or \"Gemini\".");
        }

        private static string MergeSystemPrompt(string? revitContextForSystem)
        {
            if (string.IsNullOrWhiteSpace(revitContextForSystem))
            {
                return SystemPromptBase;
            }

            return SystemPromptBase + "\n\n### Revit context (from plugin)\n" + revitContextForSystem.Trim();
        }

        private async Task<string> CompleteOllamaAsync(
            IReadOnlyList<ChatLlmTurn> turns,
            string? revitContextForSystem)
        {
            var baseUrl = NormalizeOllamaBaseUrl(_settings.OllamaBaseUrl);
            var model = string.IsNullOrWhiteSpace(_settings.OllamaModel) ? "llava" : _settings.OllamaModel.Trim();
            var url = baseUrl.TrimEnd('/') + "/api/chat";

            var messages = new List<object>
            {
                new { role = "system", content = MergeSystemPrompt(revitContextForSystem) }
            };

            foreach (var turn in turns)
            {
                var trimmed = turn.Text?.Trim();
                if (string.IsNullOrEmpty(trimmed) && string.IsNullOrEmpty(turn.ImageBase64))
                {
                    continue;
                }

                var text = string.IsNullOrEmpty(trimmed) ? "(image attached)" : trimmed;
                if (turn.IsUser && !string.IsNullOrEmpty(turn.ImageBase64))
                {
                    messages.Add(new
                    {
                        role = "user",
                        content = text,
                        images = new[] { turn.ImageBase64 }
                    });
                }
                else
                {
                    messages.Add(new
                    {
                        role = turn.IsUser ? "user" : "assistant",
                        content = text
                    });
                }
            }

            var body = new { model, stream = false, messages };
            var json = JsonConvert.SerializeObject(body);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                var response = await Http.PostAsync(url, content).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        "Ollama recusou o pedido (" + (int)response.StatusCode + ").\n\n" +
                        "Para imagens usa um modelo com visÃ£o (ex. llava, qwen2-vl). URL: " + url + "\n\n---\n" + payload);
                }

                return ExtractOllamaAssistantContent(payload);
            }
        }

        private async Task<string> CompleteGeminiAsync(
            IReadOnlyList<ChatLlmTurn> turns,
            string? revitContextForSystem)
        {
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            {
                throw new InvalidOperationException("GeminiApiKey is empty. Set it in pluginsettings.json.");
            }

            var modelId = NormalizeGeminiModelId(_settings.GeminiModel);
            var url =
                "https://generativelanguage.googleapis.com/v1beta/models/" +
                modelId +
                ":generateContent?key=" +
                Uri.EscapeDataString(_settings.GeminiApiKey);

            var contents = new List<object>();
            foreach (var turn in turns)
            {
                var trimmed = turn.Text?.Trim();
                if (string.IsNullOrEmpty(trimmed) && string.IsNullOrEmpty(turn.ImageBase64))
                {
                    continue;
                }

                var text = string.IsNullOrEmpty(trimmed) ? "(image attached)" : trimmed;
                var role = turn.IsUser ? "user" : "model";
                if (turn.IsUser && !string.IsNullOrEmpty(turn.ImageBase64))
                {
                    var mime = string.IsNullOrWhiteSpace(turn.ImageMimeType) ? "image/png" : turn.ImageMimeType;
                    contents.Add(new
                    {
                        role,
                        parts = new object[]
                        {
                            new { text },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = mime,
                                    data = turn.ImageBase64
                                }
                            }
                        }
                    });
                }
                else
                {
                    contents.Add(new
                    {
                        role,
                        parts = new object[] { new { text } }
                    });
                }
            }

            if (contents.Count == 0)
            {
                throw new InvalidOperationException("No messages to send.");
            }

            var body = new
            {
                systemInstruction = new
                {
                    parts = new object[] { new { text = MergeSystemPrompt(revitContextForSystem) } }
                },
                contents
            };

            var json = JsonConvert.SerializeObject(body);
            var response = await Http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"))
                .ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Gemini API error: " + (int)response.StatusCode + "\n" + payload);
            }

            return SketchInterpretationParser.ExtractAssistantTextFromGeminiResponse(payload);
        }

        private static string ExtractOllamaAssistantContent(string payload)
        {
            var root = JObject.Parse(payload);
            var text = root["message"]?["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Ollama returned no message.content. Resposta: " + payload);
            }

            return text.Trim();
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
    }
}
