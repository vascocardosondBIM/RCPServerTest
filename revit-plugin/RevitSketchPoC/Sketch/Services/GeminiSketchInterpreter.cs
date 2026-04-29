using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using RevitSketchPoC.Core.Configuration;
using RevitSketchPoC.Sketch.Contracts;

namespace RevitSketchPoC.Sketch.Services
{
    public sealed class GeminiSketchInterpreter : ISketchInterpreter
    {
        private readonly PluginSettings _settings;
        private static readonly HttpClient Http = new HttpClient();

        public GeminiSketchInterpreter(PluginSettings settings)
        {
            _settings = settings;
        }

        public async Task<SketchInterpretation> InterpretAsync(SketchToBimRequest request)
        {
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            {
                throw new InvalidOperationException("GeminiApiKey is empty. Set it in pluginsettings.json.");
            }

            var (base64, mimeType) = ResolveImage(request);
            var prompt = SketchLlmPrompts.BuildForSketchRequest(request);
            var modelId = NormalizeModelId(_settings.GeminiModel);
            var url =
                "https://generativelanguage.googleapis.com/v1beta/models/" +
                modelId +
                ":generateContent?key=" +
                Uri.EscapeDataString(_settings.GeminiApiKey);

            var body = new
            {
                contents = new object[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = prompt },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = mimeType,
                                    data = base64
                                }
                            }
                        }
                    }
                }
            };

            var json = JsonConvert.SerializeObject(body);
            var response = await Http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"))
                .ConfigureAwait(true);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(FormatGeminiError(response.StatusCode, payload));
            }

            var assistantText = SketchInterpretationParser.ExtractAssistantTextFromGeminiResponse(payload);
            var jsonBlock = SketchInterpretationParser.ExtractJsonObjectFromAssistantText(assistantText);
            return SketchInterpretationParser.DeserializeAndValidate(jsonBlock);
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

        private static string FormatGeminiError(HttpStatusCode status, string payload)
        {
            var summary = new System.Text.StringBuilder();
            summary.AppendLine("Gemini API recusou o pedido (" + (int)status + " " + status + ").");

            if (status == HttpStatusCode.NotFound)
            {
                summary.AppendLine("Verifica GeminiModel em pluginsettings.json (ex.: gemini-2.0-flash).");
            }

            var lower = payload.ToLowerInvariant();
            if (status == (HttpStatusCode)429 // TooManyRequests (enum value not in all .NET Framework targets)
                || lower.Contains("resource_exhausted")
                || lower.Contains("quota")
                || lower.Contains("generate_content_free_tier"))
            {
                summary.AppendLine();
                summary.AppendLine(">>> ProvÃ¡vel limite do plano gratuito (RPM/RPD) ou quota esgotada.");
                summary.AppendLine("    Espera alguns minutos, reduz pedidos, ou ativa faturaÃ§Ã£o / API key com quota maior na Google AI Studio.");
            }

            summary.AppendLine();
            summary.AppendLine("--- Resposta da API (completa) ---");

            try
            {
                var token = JToken.Parse(payload);
                summary.AppendLine(token.ToString(Formatting.Indented));
            }
            catch
            {
                summary.AppendLine(payload);
            }

            return summary.ToString().TrimEnd();
        }

        /// <summary>
        /// API expects model id without "models/" prefix (e.g. gemini-2.0-flash).
        /// </summary>
        private static string NormalizeModelId(string? model)
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
