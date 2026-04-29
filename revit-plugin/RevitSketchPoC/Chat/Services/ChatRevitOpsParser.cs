using Newtonsoft.Json.Linq;
using System;
using System.Text.RegularExpressions;

namespace RevitSketchPoC.Chat.Services
{
    /// <summary>Extracts <c>revitOps</c> JSON from assistant markdown or raw JSON.</summary>
    public static class ChatRevitOpsParser
    {
        private static readonly Regex JsonFence = new Regex(
            "```(?:json)?\\s*([\\s\\S]*?)```",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Returns null if no operations found or parse error.</summary>
        public static JArray? TryExtractRevitOps(string assistantText)
        {
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return null;
            }

            foreach (Match m in JsonFence.Matches(assistantText))
            {
                var inner = m.Groups[1].Value.Trim();
                var arr = TryParseRevitOpsFromJson(inner);
                if (arr != null && arr.Count > 0)
                {
                    return arr;
                }
            }

            var start = assistantText.LastIndexOf("{", StringComparison.Ordinal);
            if (start >= 0)
            {
                var slice = assistantText.Substring(start);
                var arr = TryParseRevitOpsFromJson(slice);
                if (arr != null && arr.Count > 0)
                {
                    return arr;
                }
            }

            return null;
        }

        private static JArray? TryParseRevitOpsFromJson(string json)
        {
            try
            {
                var token = JToken.Parse(json);
                if (token is JObject obj && obj["revitOps"] is JArray arr)
                {
                    return arr.Count > 0 ? arr : null;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }
    }
}
