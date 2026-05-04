using System.IO;
using Newtonsoft.Json;

namespace RevitSketchPoC.Core.Configuration
{
    public sealed class PluginSettings
    {
        public int TcpPort { get; set; } = 8081;

        /// <summary><c>Ollama</c> (local), <c>Gemini</c> (Google), ou <c>Nvidia</c> (integrate.api.nvidia.com, OpenAI-compatible).</summary>
        public string LlmProvider { get; set; } = "Ollama";

        public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
        public string OllamaModel { get; set; } = "llava";

        public string GeminiApiKey { get; set; } = string.Empty;
        public string GeminiModel { get; set; } = "gemini-2.0-flash";

        /// <summary>Bearer token (ex. <c>nvapi-...</c>). Não commits este valor.</summary>
        public string NvidiaApiKey { get; set; } = string.Empty;

        /// <summary>Model id on NVIDIA API (ex. <c>google/gemma-3n-e4b-it</c>).</summary>
        public string NvidiaModel { get; set; } = "google/gemma-3n-e4b-it";

        /// <summary>Full chat-completions URL; vazio = <c>https://integrate.api.nvidia.com/v1/chat/completions</c>.</summary>
        public string NvidiaChatCompletionsUrl { get; set; } = string.Empty;
        public double DefaultWallHeightMeters { get; set; } = 3.0;
    }

    public static class PluginSettingsLoader
    {
        public static PluginSettings Load(string assemblyDirectory)
        {
            var filePath = Path.Combine(assemblyDirectory, "pluginsettings.json");
            if (!File.Exists(filePath))
            {
                return new PluginSettings();
            }

            var json = File.ReadAllText(filePath);
            var settings = JsonConvert.DeserializeObject<PluginSettings>(json);
            return settings ?? new PluginSettings();
        }
    }
}
