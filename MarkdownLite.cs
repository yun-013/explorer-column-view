using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ColumnView;

/// <summary>
/// Markdown を FlowDocument に変換する軽量レンダラー (プレビュー用)。
/// 見出し・箇条書き・引用・水平線・コードブロック・太字/斜体/インラインコード/リンク
/// に対応する。厳密な CommonMark 準拠は目指さず「読みやすく見える」ことを優先。
/// </summary>
public static class MarkdownLite
{
    private static readonly FontFamily CodeFont = new("Consolas, ui-monospace, monospace");
    private static readonly Brush CodeBackground = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));

    private static readonly Regex HeaderRx = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex ListRx = new(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex HrRx = new(@"^\s*([-*_])\s*\1\s*\1[\s\-*_]*$", RegexOptions.Compiled);
    private static readonly Regex InlineRx = new(
        @"(\*\*(?<b>.+?)\*\*)|(__(?<b2>.+?)__)|(\*(?<i>[^*]+?)\*)|(`(?<c>[^`]+?)`)|(\[(?<lt>[^\]]+)\]\((?<lu>[^)\s]+)\))",
        RegexOptions.Compiled);

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(22, 18, 22, 18),
            FontSize = 13.5,
            PageWidth = double.NaN,
        };

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        bool inCode = false;
        var code = new StringBuilder();
        var para = new StringBuilder();

        void FlushPara()
        {
            if (para.Length == 0)
                return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(p.Inlines, para.ToString());
            doc.Blocks.Add(p);
            para.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    doc.Blocks.Add(MakeCodeBlock(code.ToString()));
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    FlushPara();
                    inCode = true;
                }
                continue;
            }
            if (inCode)
            {
                code.AppendLine(raw);
                continue;
            }

            if (line.Length == 0)
            {
                FlushPara();
                continue;
            }

            var m = HeaderRx.Match(line);
            if (m.Success)
            {
                FlushPara();
                int level = m.Groups[1].Value.Length;
                double[] sizes = { 24, 20, 17, 15.5, 14, 13.5 };
                var p = new Paragraph
                {
                    FontSize = sizes[level - 1],
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 6),
                };
                AddInlines(p.Inlines, m.Groups[2].Value);
                doc.Blocks.Add(p);
                continue;
            }

            if (HrRx.IsMatch(line))
            {
                FlushPara();
                doc.Blocks.Add(new BlockUIContainer(new Border
                {
                    Height = 1,
                    Background = Brushes.Gray,
                    Opacity = 0.4,
                    Margin = new Thickness(0, 4, 0, 4),
                }));
                continue;
            }

            m = ListRx.Match(line);
            if (m.Success)
            {
                FlushPara();
                int indent = m.Groups[1].Value.Length;
                var marker = m.Groups[2].Value;
                var bullet = char.IsDigit(marker[0]) ? marker + " " : "• ";
                var p = new Paragraph { Margin = new Thickness(14 + indent * 8, 0, 0, 3) };
                p.Inlines.Add(new Run(bullet));
                AddInlines(p.Inlines, m.Groups[3].Value);
                doc.Blocks.Add(p);
                continue;
            }

            if (line.StartsWith(">", StringComparison.Ordinal))
            {
                FlushPara();
                var p = new Paragraph
                {
                    Margin = new Thickness(14, 0, 0, 6),
                    FontStyle = FontStyles.Italic,
                    Foreground = Brushes.Gray,
                };
                AddInlines(p.Inlines, line.TrimStart('>', ' '));
                doc.Blocks.Add(p);
                continue;
            }

            // 通常行: 空行までを 1 段落として連結
            if (para.Length > 0)
                para.Append(' ');
            para.Append(line);
        }
        FlushPara();
        if (inCode && code.Length > 0)
            doc.Blocks.Add(MakeCodeBlock(code.ToString()));

        return doc;
    }

    private static Paragraph MakeCodeBlock(string text) => new(new Run(text.TrimEnd('\n', '\r')))
    {
        FontFamily = CodeFont,
        FontSize = 12,
        Background = CodeBackground,
        Padding = new Thickness(10),
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static void AddInlines(InlineCollection inlines, string text)
    {
        int pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > pos)
                inlines.Add(new Run(text[pos..m.Index]));

            if (m.Groups["b"].Success)
                inlines.Add(new Bold(new Run(m.Groups["b"].Value)));
            else if (m.Groups["b2"].Success)
                inlines.Add(new Bold(new Run(m.Groups["b2"].Value)));
            else if (m.Groups["i"].Success)
                inlines.Add(new Italic(new Run(m.Groups["i"].Value)));
            else if (m.Groups["c"].Success)
                inlines.Add(new Run(m.Groups["c"].Value) { FontFamily = CodeFont, Background = CodeBackground });
            else if (m.Groups["lt"].Success)
            {
                var url = m.Groups["lu"].Value;
                var link = new Hyperlink(new Run(m.Groups["lt"].Value)) { ToolTip = url };
                link.Click += (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch { /* 開けない URL は無視 */ }
                };
                inlines.Add(link);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            inlines.Add(new Run(text[pos..]));
    }
}
