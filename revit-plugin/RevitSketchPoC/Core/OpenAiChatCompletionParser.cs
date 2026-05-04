using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RevitSketchPoC.Core
{
    /// <summary>Parses non-streaming <c>/v1/chat/completions</c> JSON (OpenAI-compatible).</summary>
    internal static class OpenAiChatCompletionParser
    {
        public static string ExtractAssistantContent(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                throw new InvalidOperationException("Empty API response body.");
            }

            var root = JObject.Parse(payload);
            var err = root["error"];
            if (err != null)
            {
                var msg = err["message"]?.ToString() ?? err.ToString();
                throw new InvalidOperationException("API error: " + msg.Trim());
            }

            var content = root["choices"]?[0]?["message"]?["content"];
            if (content == null)
            {
                throw new InvalidOperationException("No choices[0].message.content. Body: " + Truncate(payload, 2000));
            }

            if (content.Type == JTokenType.String)
            {
                var s = content.ToString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    throw new InvalidOperationException("Assistant content was empty.");
                }

                return s.Trim();
            }

            if (content.Type == JTokenType.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content)
                {
                    if (string.Equals(part["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = part["text"]?.ToString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            sb.Append(t);
                        }
                    }
                }

                var joined = sb.ToString().Trim();
                if (string.IsNullOrEmpty(joined))
                {
                    throw new InvalidOperationException("Assistant content array had no text parts.");
                }

                return joined;
            }

            throw new InvalidOperationException("Unexpected message.content shape: " + content.Type);
        }

        private static string Truncate(string s, int max)
        {
            if (s.Length <= max)
            {
                return s;
            }

            return s.Substring(0, max) + "…";
        }
    }
}
