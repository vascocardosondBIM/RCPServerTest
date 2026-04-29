using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace RevitSketchPoC.Services
{
    /// <summary>
    /// Text-only multi-turn chat using the same <see cref="PluginSettings.LlmProvider"/> as sketch interpretation.
    /// </summary>
    public sealed class LlmChatService
    {
        private const string SystemPrompt =
            "You are a helpful assistant for Autodesk Revit users (architecture / BIM). " +
            "Answer clearly and concisely. You cannot run Revit commands or read the model in this chat mode; " +
            "you only provide guidance and explanations.";

        private readonly PluginSettings _settings;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        public LlmChatService(PluginSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Chronological turns: each item is (isUser, text). Last turn must be from the user.
        /// </summary>
        public Task<string> CompleteAsync(IReadOnlyList<(bool isUser, string text)> turns)
        {
            var provider = string.IsNullOrWhiteSpace(_settings.LlmProvider)
                ? "Ollama"
                : _settings.LlmProvider.Trim();

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return CompleteOllamaAsync(turns);
            }

            if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return CompleteGeminiAsync(turns);
            }

            throw new InvalidOperationException(
                "Unknown LlmProvider in pluginsettings.json: \"" + provider + "\". Use \"Ollama\" or \"Gemini\".");
        }

        private async Task<string> CompleteOllamaAsync(IReadOnlyList<(bool isUser, string text)> turns)
        {
            var baseUrl = NormalizeOllamaBaseUrl(_settings.OllamaBaseUrl);
            var model = string.IsNullOrWhiteSpace(_settings.OllamaModel) ? "llava" : _settings.OllamaModel.Trim();
            var url = baseUrl.TrimEnd('/') + "/api/chat";

            var messages = new List<object>
            {
                new { role = "system", content = SystemPrompt }
            };

            foreach (var (isUser, text) in turns)
            {
                var trimmed = text?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                messages.Add(new
                {
                    role = isUser ? "user" : "assistant",
                    content = trimmed
                });
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
                        "Confirma que o Ollama está a correr.\nURL: " + url + "\n\n---\n" + payload);
                }

                return ExtractOllamaAssistantContent(payload);
            }
        }

        private async Task<string> CompleteGeminiAsync(IReadOnlyList<(bool isUser, string text)> turns)
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
            foreach (var (isUser, text) in turns)
            {
                var trimmed = text?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                var role = isUser ? "user" : "model";
                contents.Add(new
                {
                    role,
                    parts = new object[] { new { text = trimmed } }
                });
            }

            if (contents.Count == 0)
            {
                throw new InvalidOperationException("No messages to send.");
            }

            var body = new
            {
                systemInstruction = new
                {
                    parts = new object[] { new { text = SystemPrompt } }
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
