using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitSketchPoC.Chat.Services
{
    /// <summary>One segment of a chat bubble (prose vs fenced code).</summary>
    public sealed class ChatMessageBlock
    {
        public ChatMessageBlock(bool isCode, string text)
        {
            IsCode = isCode;
            Text = text ?? string.Empty;
        }

        public bool IsCode { get; }

        public string Text { get; }
    }

    /// <summary>Splits assistant/user text into prose vs fenced code blocks for WPF display (monospace for ```).</summary>
    public static class ChatMessageMarkdownFormatter
    {
        private const int MaxCharsForMarkdownSplit = 350_000;

        /// <summary>Opening fence, optional info string, newline, body until closing ```.</summary>
        private static readonly Regex FencedCode = new Regex(
            @"```(?:[^\r\n`]*)\r?\n(.*?)```",
            RegexOptions.Singleline | RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));

        /// <summary>Paragraph breaks (blank lines) for splitting prose vs likely ASCII diagrams.</summary>
        private static readonly Regex ParagraphBreak = new Regex(@"\r?\n\r?\n+", RegexOptions.Compiled);

        /// <summary>Builds ordered parts: prose (Segoe) and code (Consolas).</summary>
        public static IReadOnlyList<ChatMessageBlock> SplitToParts(string? raw)
        {
            var text = raw ?? string.Empty;
            var list = new List<ChatMessageBlock>();
            if (string.IsNullOrEmpty(text))
            {
                return list;
            }

            if (text.Length > MaxCharsForMarkdownSplit)
            {
                list.Add(new ChatMessageBlock(false, text));
                return list;
            }

            MatchCollection matches;
            try
            {
                matches = FencedCode.Matches(text);
            }
            catch (RegexMatchTimeoutException)
            {
                list.Add(new ChatMessageBlock(false, text));
                return list;
            }

            if (matches.Count == 0)
            {
                list.Add(new ChatMessageBlock(false, text));
                return list;
            }

            var idx = 0;
            foreach (Match m in matches)
            {
                if (m.Index > idx)
                {
                    var prose = text.Substring(idx, m.Index - idx);
                    AppendProse(list, prose);
                }

                var code = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                code = code.TrimEnd('\r', '\n');
                if (code.Length > 0)
                {
                    list.Add(new ChatMessageBlock(true, code));
                }

                idx = m.Index + m.Length;
            }

            if (idx < text.Length)
            {
                AppendProse(list, text.Substring(idx));
            }

            if (list.Count == 0)
            {
                list.Add(new ChatMessageBlock(false, text));
            }

            return list;
        }

        private static void AppendProse(List<ChatMessageBlock> list, string prose)
        {
            if (string.IsNullOrEmpty(prose))
            {
                return;
            }

            foreach (var segment in SplitProseByParagraphsAndDiagrams(prose))
            {
                list.Add(segment);
            }
        }

        /// <summary>
        /// Splits prose on blank lines; paragraphs that look like box/flow ASCII art use monospace
        /// (same as fenced code) so pipes and dashes align.
        /// </summary>
        private static IEnumerable<ChatMessageBlock> SplitProseByParagraphsAndDiagrams(string prose)
        {
            var parts = ParagraphBreak.Split(prose);
            for (var i = 0; i < parts.Length; i++)
            {
                var raw = parts[i];
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                var trimmed = raw.Trim('\r', '\n');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var isDiagram = IsLikelyAsciiDiagram(trimmed);
                yield return new ChatMessageBlock(isDiagram, trimmed);
            }
        }

        private static bool IsLikelyAsciiDiagram(string block)
        {
            var lines = block.Replace("\r\n", "\n").Split('\n');
            var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
            if (nonEmpty.Count < 2)
            {
                return false;
            }

            var score = 0;
            foreach (var line in nonEmpty)
            {
                var t = line.TrimEnd();
                if (t.IndexOf('|') >= 0)
                {
                    score++;
                    continue;
                }

                if (Regex.IsMatch(t, @"[\-─═]{5,}"))
                {
                    score++;
                    continue;
                }

                if (Regex.IsMatch(t, @"[┌┐└┘├┤╔╗╚╝║]+"))
                {
                    score++;
                    continue;
                }

                if (Regex.IsMatch(t, @"^\s*[\[\]()+]{2,}"))
                {
                    score++;
                }
            }

            return score >= 2 && score * 2 >= nonEmpty.Count;
        }
    }
}
