using Newtonsoft.Json;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using RevitSketchPoC.Core;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Sketch.Contracts;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>Sketch → BIM via NVIDIA <see href="https://integrate.api.nvidia.com/v1/chat/completions">OpenAI-compatible</see> API.</summary>
    public sealed class NvidiaSketchInterpreter : ISketchInterpreter
    {
        private readonly PluginSettings _settings;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

        public NvidiaSketchInterpreter(PluginSettings settings)
        {
            _settings = settings;
        }

        public async Task<SketchInterpretation> InterpretAsync(SketchToBimRequest request)
        {
            if (string.IsNullOrWhiteSpace(_settings.NvidiaApiKey))
            {
                throw new InvalidOperationException("NvidiaApiKey is empty. Set it in pluginsettings.json.");
            }

            var (base64, mimeType) = ResolveImage(request);
            var prompt = SketchLlmPrompts.BuildForSketchRequest(request);
            var url = NormalizeChatCompletionsUrl(_settings.NvidiaChatCompletionsUrl);
            var model = string.IsNullOrWhiteSpace(_settings.NvidiaModel)
                ? "google/gemma-3n-e4b-it"
                : _settings.NvidiaModel.Trim();

            var dataUrl = "data:" + mimeType + ";base64," + base64;
            var messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            };

            var body = new
            {
                model,
                messages,
                stream = false,
                max_tokens = 8192,
                temperature = 0.2,
                top_p = 0.7,
                frequency_penalty = 0.0,
                presence_penalty = 0.0
            };

            var json = JsonConvert.SerializeObject(body);
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.NvidiaApiKey.Trim());
                req.Headers.Accept.ParseAdd("application/json");
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await Http.SendAsync(req).ConfigureAwait(true);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        "NVIDIA API recusou o pedido (" + (int)response.StatusCode + "). URL: " + url + "\n\n---\n" + payload);
                }

                var assistantText = OpenAiChatCompletionParser.ExtractAssistantContent(payload);
                var jsonBlock = SketchInterpretationParser.ExtractJsonObjectFromAssistantText(assistantText);
                return SketchInterpretationParser.DeserializeAndValidate(jsonBlock);
            }
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
            var ext = Path.GetExtension(request.ImagePath)?.ToLowerInvariant();
            var mimeType = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : "image/png";
            return (Convert.ToBase64String(bytes), mimeType);
        }

        private static string NormalizeChatCompletionsUrl(string? url)
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
    }
}
