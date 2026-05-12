using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace RevitSketchPoC.Chat.Services
{
    /// <summary>Builds a read-only WPF <see cref="FlowDocument"/> from Markdown (Markdig).</summary>
    public static class ChatMarkdownToFlowDocument
    {
        private static readonly SolidColorBrush CodeBackground =
            new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));

        private static readonly SolidColorBrush LinkBrush =
            new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));

        private static readonly SolidColorBrush RuleBrush =
            new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));

        static ChatMarkdownToFlowDocument()
        {
            CodeBackground.Freeze();
            LinkBrush.Freeze();
            RuleBrush.Freeze();
        }

        public static FlowDocument Build(string? markdown)
        {
            var md = markdown ?? string.Empty;
            var textBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
            textBrush.Freeze();

            var flow = new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                Foreground = textBrush,
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left,
                PageWidth = 468
            };

            if (string.IsNullOrWhiteSpace(md))
            {
                flow.Blocks.Add(new Paragraph(new Run(" ")));
                return flow;
            }

            var pipeline = new MarkdownPipelineBuilder()
                .UseSoftlineBreakAsHardlineBreak()
                .Build();
            var document = Markdown.Parse(md, pipeline);
            foreach (var block in document)
            {
                AppendBlock(flow, block);
            }

            if (flow.Blocks.Count == 0)
            {
                flow.Blocks.Add(new Paragraph(new Run(md)));
            }

            return flow;
        }

        private static void AppendBlock(FlowDocument flow, MdBlock block)
        {
            switch (block)
            {
                case ParagraphBlock pb:
                    flow.Blocks.Add(CreateParagraphFromInline(pb.Inline, 14));
                    break;
                case HeadingBlock hb:
                {
                    var fontSize = hb.Level switch
                    {
                        1 => 22.0,
                        2 => 18.0,
                        3 => 16.0,
                        4 => 15.0,
                        _ => 14.0
                    };
                    var p = CreateParagraphFromInline(hb.Inline, fontSize);
                    p.FontWeight = FontWeights.SemiBold;
                    p.Margin = new Thickness(0, hb.Level <= 2 ? 10 : 6, 0, 4);
                    flow.Blocks.Add(p);
                    break;
                }
                case FencedCodeBlock fcb:
                    flow.Blocks.Add(CreateCodeParagraph(GetFencedCodeText(fcb)));
                    break;
                case CodeBlock cb:
                    flow.Blocks.Add(CreateCodeParagraph(GetIndentedCodeText(cb)));
                    break;
                case ThematicBreakBlock:
                    flow.Blocks.Add(CreateHorizontalRule());
                    break;
                case QuoteBlock quote:
                    AppendQuote(flow, quote);
                    break;
                case ListBlock list:
                    AppendList(flow, list);
                    break;
                default:
                    if (block is ContainerBlock container)
                    {
                        foreach (var child in container)
                        {
                            AppendBlock(flow, child);
                        }
                    }

                    break;
            }
        }

        private static void AppendQuote(FlowDocument flow, QuoteBlock quote)
        {
            var section = new Section
            {
                BorderBrush = RuleBrush,
                BorderThickness = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(10, 4, 0, 4),
                Margin = new Thickness(0, 4, 0, 4)
            };
            foreach (var child in quote)
            {
                AppendBlockToContainer(section.Blocks, child);
            }

            if (section.Blocks.Count > 0)
            {
                flow.Blocks.Add(section);
            }
        }

        private static void AppendBlockToContainer(BlockCollection blocks, MdBlock block)
        {
            switch (block)
            {
                case ParagraphBlock pb:
                    blocks.Add(CreateParagraphFromInline(pb.Inline, 14));
                    break;
                case HeadingBlock hb:
                {
                    var p = CreateParagraphFromInline(hb.Inline, hb.Level <= 3 ? 16.0 : 14.0);
                    p.FontWeight = FontWeights.SemiBold;
                    blocks.Add(p);
                    break;
                }
                case FencedCodeBlock fcb:
                    blocks.Add(CreateCodeParagraph(GetFencedCodeText(fcb)));
                    break;
                case CodeBlock cb:
                    blocks.Add(CreateCodeParagraph(GetIndentedCodeText(cb)));
                    break;
                case ThematicBreakBlock:
                    blocks.Add(CreateHorizontalRule());
                    break;
                case QuoteBlock q:
                    var nested = new FlowDocument();
                    AppendQuote(nested, q);
                    foreach (var b in nested.Blocks)
                    {
                        blocks.Add(b);
                    }

                    break;
                case ListBlock list:
                    AppendListToContainer(blocks, list);
                    break;
                default:
                    if (block is ContainerBlock container)
                    {
                        foreach (var child in container)
                        {
                            AppendBlockToContainer(blocks, child);
                        }
                    }

                    break;
            }
        }

        private static void AppendList(FlowDocument flow, ListBlock list)
        {
            AppendListToContainer(flow.Blocks, list);
        }

        private static void AppendListToContainer(BlockCollection blocks, ListBlock list)
        {
            var ordered = list.IsOrdered;
            var itemNum = 1;
            if (ordered)
            {
                var startStr = list.OrderedStart?.ToString() ?? "1";
                if (!int.TryParse(startStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out itemNum) || itemNum < 1)
                {
                    itemNum = 1;
                }
            }

            foreach (var item in list)
            {
                if (item is not ListItemBlock lib)
                {
                    continue;
                }

                var prefix = ordered ? itemNum.ToString(CultureInfo.InvariantCulture) + ". " : "• ";
                if (ordered)
                {
                    itemNum++;
                }

                foreach (var inner in lib)
                {
                    if (inner is ParagraphBlock pb)
                    {
                        var p = CreateParagraphFromInline(pb.Inline, 14);
                        p.Margin = new Thickness(16, 2, 0, 2);
                        p.TextIndent = -10;
                        p.Inlines.InsertBefore(p.Inlines.FirstInline, new Run(prefix) { FontWeight = FontWeights.SemiBold });
                        blocks.Add(p);
                    }
                    else if (inner is ListBlock nested)
                    {
                        AppendListToContainer(blocks, nested);
                    }
                    else
                    {
                        var wrap = new Paragraph(new Run(prefix + DescribeBlock(inner)))
                        {
                            Margin = new Thickness(16, 2, 0, 2)
                        };
                        blocks.Add(wrap);
                    }
                }
            }
        }

        private static string DescribeBlock(MdBlock b) =>
            b is LeafBlock l ? l.ToString() : b.GetType().Name;

        private static Paragraph CreateCodeParagraph(string code)
        {
            var text = string.IsNullOrEmpty(code) ? " " : code;
            var p = new Paragraph(new Run(text))
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                LineHeight = 18,
                Background = CodeBackground,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 6, 0, 6)
            };
            return p;
        }

        private static System.Windows.Documents.Block CreateHorizontalRule()
        {
            var line = new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Fill = RuleBrush,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SnapsToDevicePixels = true
            };
            var ui = new BlockUIContainer(line) { Margin = new Thickness(0) };
            return ui;
        }

        private static string GetFencedCodeText(FencedCodeBlock block) => JoinStringLines(block.Lines);

        private static string GetIndentedCodeText(CodeBlock block) => JoinStringLines(block.Lines);

        private static string JoinStringLines(Markdig.Helpers.StringLineGroup lines)
        {
            var sb = new StringBuilder();
            var first = true;
            foreach (var line in lines)
            {
                if (!first)
                {
                    sb.AppendLine();
                }

                first = false;
                sb.Append(line.ToString());
            }

            return sb.ToString();
        }

        private static Paragraph CreateParagraphFromInline(ContainerInline? inline, double fontSize)
        {
            var p = new Paragraph { FontSize = fontSize, Margin = new Thickness(0, 2, 0, 4) };
            if (inline == null)
            {
                p.Inlines.Add(new Run(" "));
                return p;
            }

            AddInlines(p.Inlines, inline.FirstChild);
            if (p.Inlines.FirstInline == null)
            {
                p.Inlines.Add(new Run(" "));
            }

            return p;
        }

        private static void AddInlines(InlineCollection target, MdInline? node)
        {
            while (node != null)
            {
                switch (node)
                {
                    case LiteralInline lit:
                    {
                        var slice = lit.Content;
                        var s = slice.Text.Substring(slice.Start, slice.Length);
                        if (s.Length > 0)
                        {
                            target.Add(new Run(s));
                        }

                        break;
                    }
                    case LineBreakInline:
                        target.Add(new LineBreak());
                        break;
                    case CodeInline code:
                        target.Add(new Run(code.Content)
                        {
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 12.5,
                            Background = CodeBackground
                        });
                        break;
                    case EmphasisInline emph:
                    {
                        var span = new Span();
                        AddInlines(span.Inlines, emph.FirstChild);
                        if (emph.DelimiterCount >= 2)
                        {
                            span.FontWeight = FontWeights.Bold;
                        }
                        else
                        {
                            span.FontStyle = FontStyles.Italic;
                        }

                        target.Add(span);
                        break;
                    }
                    case LinkInline link when !link.IsImage:
                    {
                        var h = new Hyperlink();
                        AddInlines(h.Inlines, link.FirstChild);
                        if (h.Inlines.FirstInline == null && !string.IsNullOrEmpty(link.Url))
                        {
                            h.Inlines.Add(new Run(link.Url));
                        }

                        h.Foreground = LinkBrush;
                        if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
                        {
                            h.NavigateUri = uri;
                            h.RequestNavigate += (_, e) =>
                            {
                                try
                                {
                                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                                }
                                catch
                                {
                                    // ignore
                                }

                                e.Handled = true;
                            };
                        }

                        target.Add(h);
                        break;
                    }
                    case LinkInline linkImg when linkImg.IsImage:
                        target.Add(new Run(string.IsNullOrEmpty(linkImg.Title) ? "[imagem]" : linkImg.Title)
                        {
                            FontStyle = FontStyles.Italic,
                            Foreground = LinkBrush
                        });
                        break;
                    case AutolinkInline auto:
                        target.Add(new Run(auto.Url) { Foreground = LinkBrush });
                        break;
                    case HtmlInline html:
                    {
                        var t = html.Tag;
                        if (t.Length > 0)
                        {
                            target.Add(new Run(t.ToString()));
                        }

                        break;
                    }
                    case ContainerInline container:
                        AddInlines(target, container.FirstChild);
                        break;
                    default:
                    {
                        if (node is LeafInline leaf)
                        {
                            var slice = leaf.ToString();
                            if (!string.IsNullOrEmpty(slice))
                            {
                                target.Add(new Run(slice));
                            }
                        }

                        break;
                    }
                }

                node = node.NextSibling;
            }
        }
    }
}
