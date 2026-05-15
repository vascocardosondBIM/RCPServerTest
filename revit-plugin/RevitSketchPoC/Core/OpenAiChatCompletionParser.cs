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
            if (content == null || content.Type == JTokenType.Null)
            {
                var reasoningContent = root["choices"]?[0]?["message"]?["reasoning_content"]?.ToString();
                if (!string.IsNullOrWhiteSpace(reasoningContent))
                {
                    var trimmedReasoning = reasoningContent.Trim();
                    if (LooksLikeJson(trimmedReasoning))
                    {
                        return trimmedReasoning;
                    }

                    var extracted = TryExtractBalancedJson(trimmedReasoning);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        return extracted;
                    }

                    throw new InvalidOperationException(
                        "Unexpected message.content shape: Null. " +
                        "Provider returned only reasoning_content without final content. " +
                        "Try disabling thinking/reasoning for this flow or reducing reasoning budget.");
                }

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

        private static bool LooksLikeJson(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var s = value.TrimStart();
            return s.StartsWith("{", StringComparison.Ordinal) || s.StartsWith("[", StringComparison.Ordinal);
        }

        private static string? TryExtractBalancedJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var cleaned = text
                .Replace("```json", string.Empty)
                .Replace("```", string.Empty);

            var start = -1;
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = 0; i < cleaned.Length; i++)
            {
                var c = cleaned[i];

                if (start < 0)
                {
                    if (c == '{' || c == '[')
                    {
                        start = i;
                        depth = 1;
                    }

                    continue;
                }

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{' || c == '[')
                {
                    depth++;
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        return cleaned.Substring(start, i - start + 1).Trim();
                    }
                }
            }

            return null;
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
