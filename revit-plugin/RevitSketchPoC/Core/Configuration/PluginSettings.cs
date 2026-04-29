using System.IO;
using Newtonsoft.Json;

namespace RevitSketchPoC.Core.Configuration
{
    public sealed class PluginSettings
    {
        public int TcpPort { get; set; } = 8081;

        /// <summary>Ollama (local, sem key) ou Gemini (cloud, precisa API key).</summary>
        public string LlmProvider { get; set; } = "Ollama";

        public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
        public string OllamaModel { get; set; } = "llava";

        public string GeminiApiKey { get; set; } = string.Empty;
        public string GeminiModel { get; set; } = "gemini-2.0-flash";
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
