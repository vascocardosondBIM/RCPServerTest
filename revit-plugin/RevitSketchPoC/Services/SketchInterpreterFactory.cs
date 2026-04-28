using System;

namespace RevitSketchPoC.Services
{
    public static class SketchInterpreterFactory
    {
        public static ISketchInterpreter Create(PluginSettings settings)
        {
            var provider = string.IsNullOrWhiteSpace(settings.LlmProvider)
                ? "Ollama"
                : settings.LlmProvider.Trim();

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return new OllamaSketchInterpreter(settings);
            }

            if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return new GeminiSketchInterpreter(settings);
            }

            throw new InvalidOperationException(
                "Unknown LlmProvider in pluginsettings.json: \"" + provider + "\". Use \"Ollama\" or \"Gemini\".");
        }
    }
}
