using System;
using RevitSketchPoC.Core.Configuration;

namespace RevitSketchPoC.Sketch.Services
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

            if (string.Equals(provider, "Nvidia", StringComparison.OrdinalIgnoreCase))
            {
                return new NvidiaSketchInterpreter(settings);
            }

            throw new InvalidOperationException(
                "Unknown LlmProvider in pluginsettings.json: \"" + provider + "\". Use \"Ollama\", \"Gemini\", or \"Nvidia\".");
        }
    }
}
