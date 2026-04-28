using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using RevitSketchPoC.Contracts;

namespace RevitSketchPoC.Services
{
    /// <summary>
    /// Local vision LLM via Ollama (<c>http://localhost:11434/api/chat</c>). No API key.
    /// Use a <b>local</b> vision model (e.g. <c>llava</c>, <c>moondream</c>, <c>qwen2-vl</c>), not <c>*-cloud</c> variants.
    /// </summary>
    public sealed class OllamaSketchInterpreter : ISketchInterpreter
    {
        private readonly PluginSettings _settings;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

        public OllamaSketchInterpreter(PluginSettings settings)
        {
            _settings = settings;
        }

        public async Task<SketchInterpretation> InterpretAsync(SketchToBimRequest request)
        {
            var (base64, _) = ResolveImage(request);
            var prompt = SketchLlmPrompts.BuildForSketchRequest(request);
            var baseUrl = NormalizeBaseUrl(_settings.OllamaBaseUrl);
            var model = string.IsNullOrWhiteSpace(_settings.OllamaModel) ? "llava" : _settings.OllamaModel.Trim();
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
                        content = prompt,
                        images = new[] { base64 }
                    }
                }
            };

            var json = JsonConvert.SerializeObject(body);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                var response = await Http.PostAsync(url, content).ConfigureAwait(true);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        "Ollama recusou o pedido (" + (int)response.StatusCode + ").\n\n" +
                        "Confirma que o Ollama está a correr e que fizeste `ollama pull " + model + "`.\n" +
                        "URL: " + url + "\n\n---\n" + payload);
                }

                var assistantText = ExtractAssistantContent(payload);
                var jsonBlock = SketchInterpretationParser.ExtractJsonObjectFromAssistantText(assistantText);
                return SketchInterpretationParser.DeserializeAndValidate(jsonBlock);
            }
        }

        private static string NormalizeBaseUrl(string? url)
        {
            var u = string.IsNullOrWhiteSpace(url) ? "http://localhost:11434" : url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                u = "http://" + u;
            }
            return u;
        }

        private static string ExtractAssistantContent(string payload)
        {
            var root = JObject.Parse(payload);
            var message = root["message"];
            var text = message?["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Ollama returned no message.content. Resposta: " + payload);
            }

            return text;
        }

        private static (string Base64, string MimeType) ResolveImage(SketchToBimRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.ImageBase64))
            {
                return (request.ImageBase64, string.IsNullOrWhiteSpace(request.MimeType) ? "image/png" : request.MimeType);
            }

            if (string.IsNullOrWhiteSpace(request.ImagePath))
            {
                throw new InvalidOperationException("Provide imagePath or imageBase64.");
            }

            if (!File.Exists(request.ImagePath))
            {
                throw new FileNotFoundException("Sketch image file not found.", request.ImagePath);
            }

            var bytes = File.ReadAllBytes(request.ImagePath);
            return (Convert.ToBase64String(bytes), "image/png");
        }

    }
}
