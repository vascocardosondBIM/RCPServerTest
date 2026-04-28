using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using RevitSketchPoC.Contracts;

namespace RevitSketchPoC.Services
{
    /// <summary>
    /// Shared parsing from LLM text / Gemini envelope into <see cref="SketchInterpretation"/>.
    /// </summary>
    public static class SketchInterpretationParser
    {
        public static string ExtractAssistantTextFromGeminiResponse(string payload)
        {
            var root = JObject.Parse(payload);
            var candidates = root["candidates"] as JArray;
            if (candidates == null || candidates.Count == 0)
            {
                throw new InvalidOperationException("Gemini returned no candidates.");
            }

            var parts = candidates[0]?["content"]?["parts"] as JArray;
            if (parts == null || parts.Count == 0)
            {
                throw new InvalidOperationException("Gemini returned no content parts.");
            }

            var text = string.Concat(parts
                .Select(x => x?["text"]?.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x)));

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Gemini returned empty text.");
            }

            return text.Trim();
        }

        public static string ExtractJsonObjectFromAssistantText(string assistantText)
        {
            var cleaned = assistantText.Trim();
            cleaned = cleaned.Replace("```json", string.Empty).Replace("```", string.Empty).Trim();

            var start = cleaned.IndexOf('{');
            var end = cleaned.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                cleaned = cleaned.Substring(start, end - start + 1);
            }

            return cleaned;
        }

        public static SketchInterpretation DeserializeAndValidate(string jsonObject)
        {
            var interpretation = JsonConvert.DeserializeObject<SketchInterpretation>(jsonObject);
            if (interpretation == null)
            {
                throw new InvalidOperationException("Could not parse JSON into SketchInterpretation.");
            }

            if (interpretation.Walls.Count == 0)
            {
                throw new InvalidOperationException("Model returned zero walls. Refine image or prompt.");
            }

            return interpretation;
        }
    }
}
