using System.Text;

namespace Oadm.Client.Controls;

/// <summary>A piece of a line of Markdown: plain, bold, italic or code.</summary>
public sealed record MarkdownSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false);

/// <summary>A block of a Markdown text (<see cref="MarkdownDocument.Parse"/>).</summary>
public abstract record MarkdownBlock;

public sealed record MarkdownParagraph(IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

/// <summary>A list; each item its spans; <paramref name="Ordered"/>: numbered from <paramref name="Start"/>.</summary>
public sealed record MarkdownList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MarkdownSpan>> Items) : MarkdownBlock;

public sealed record MarkdownCode(string Text, string Language) : MarkdownBlock;

/// <summary>A table: the header cells, the rows, and which columns are right-aligned (<c>---:</c>).</summary>
public sealed record MarkdownTable(IReadOnlyList<IReadOnlyList<MarkdownSpan>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownSpan>>> Rows,
    IReadOnlyList<bool> RightAligned) : MarkdownBlock;

/// <summary>
/// The Markdown subset the Assistant's answers use (docs/spec/assistant.md "What the panel shows"): paragraphs, headings,
/// lists (<c>-</c>, <c>*</c>, <c>1.</c>), bold, italic, inline code, fenced code blocks and pipe tables. Anything else stays
/// plain text. No UI: <see cref="MarkdownText"/> draws the blocks. A text that is still streaming parses too (an open code
/// block runs to the end, a table without its separator line is a paragraph).
/// </summary>
public static class MarkdownDocument
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(markdown))
        {
            return blocks;
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new StringBuilder();
        List<IReadOnlyList<MarkdownSpan>>? items = null;
        var ordered = false;
        var start = 1;

        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new MarkdownParagraph(Inline(paragraph.ToString())));
                paragraph.Clear();
            }
        }

        void FlushList()
        {
            if (items is not null)
            {
                blocks.Add(new MarkdownList(ordered, start, items));
                items = null;
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                var language = trimmed[3..].Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Append(code.Length > 0 ? "\n" : string.Empty).Append(lines[i]);
                    i++;
                }

                blocks.Add(new MarkdownCode(code.ToString(), language));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            if (IsTableRow(trimmed) && i + 1 < lines.Length && IsSeparator(lines[i + 1].Trim()))
            {
                FlushParagraph();
                FlushList();
                var header = Cells(trimmed);
                var right = Cells(lines[i + 1].Trim()).Select(c => c.EndsWith(':')).ToList();
                var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownSpan>>>();
                i += 2;
                while (i < lines.Length && IsTableRow(lines[i].Trim()))
                {
                    var cells = Cells(lines[i].Trim());
                    rows.Add([.. Enumerable.Range(0, header.Count).Select(c => Inline(c < cells.Count ? cells[c] : string.Empty))]);
                    i++;
                }

                i--;
                blocks.Add(new MarkdownTable([.. header.Select(Inline)], rows, [.. Enumerable.Range(0, header.Count).Select(c => c < right.Count && right[c])]));
                continue;
            }

            if (Heading(trimmed) is { } heading)
            {
                FlushParagraph();
                FlushList();
                blocks.Add(heading);
                continue;
            }

            if (ListItem(trimmed) is { } item)
            {
                FlushParagraph();
                if (items is null || ordered != item.Ordered)
                {
                    FlushList();
                    items = [];
                    ordered = item.Ordered;
                    start = item.Number;
                }

                items.Add(Inline(item.Text));
                continue;
            }

            if (items is not null && line.StartsWith("  ", StringComparison.Ordinal))
            {
                // An item that continues on an indented line.
                var last = items[^1];
                items[^1] = [.. last, new MarkdownSpan(" "), .. Inline(trimmed)];
                continue;
            }

            FlushList();
            paragraph.Append(paragraph.Length > 0 ? " " : string.Empty).Append(trimmed);
        }

        FlushParagraph();
        FlushList();
        return blocks;
    }

    /// <summary>The plain text of spans (tests, copy).</summary>
    public static string PlainText(IEnumerable<MarkdownSpan> spans) => string.Concat(spans.Select(s => s.Text));

    /// <summary>Bold (<c>**</c>, <c>__</c>), italic (<c>*</c>, <c>_</c> between words) and code (<c>`</c>) in one line.</summary>
    public static IReadOnlyList<MarkdownSpan> Inline(string text)
    {
        var spans = new List<MarkdownSpan>();
        var plain = new StringBuilder();
        var bold = false;
        var italic = false;

        void Flush()
        {
            if (plain.Length > 0)
            {
                spans.Add(new MarkdownSpan(plain.ToString(), bold, italic));
                plain.Clear();
            }
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_|#-".Contains(text[i + 1], StringComparison.Ordinal))
            {
                plain.Append(text[++i]);
                continue;
            }

            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    spans.Add(new MarkdownSpan(text[(i + 1)..end], bold, italic, Code: true));
                    i = end;
                    continue;
                }
            }

            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                // A closing marker, or an opening one with its closing one later in the line.
                if (bold || text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal) > i + 2)
                {
                    Flush();
                    bold = !bold;
                    i++;
                    continue;
                }
            }
            else if (c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) || italic)))
            {
                bool closes = italic && i > 0 && text[i - 1] != ' ';
                bool opens = !italic && i + 1 < text.Length && text[i + 1] != ' ' && text.IndexOf(c, i + 1) > i + 1;
                if (closes || opens)
                {
                    Flush();
                    italic = !italic;
                    continue;
                }
            }

            plain.Append(c);
        }

        Flush();
        return spans;
    }

    private static MarkdownHeading? Heading(string line)
    {
        int level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        return level is >= 1 and <= 6 && level < line.Length && line[level] == ' ' ? new MarkdownHeading(level, Inline(line[(level + 1)..].Trim())) : null;
    }

    private static (bool Ordered, int Number, string Text)? ListItem(string line)
    {
        if (line.Length > 2 && (line[0] is '-' or '*' or '+') && line[1] == ' ')
        {
            return (false, 1, line[2..].Trim());
        }

        int digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        return digits is > 0 and < 10 && digits + 1 < line.Length && line[digits] is '.' or ')' && line[digits + 1] == ' '
            ? (true, int.Parse(line[..digits], System.Globalization.CultureInfo.InvariantCulture), line[(digits + 2)..].Trim())
            : null;
    }

    private static bool IsTableRow(string line) => line.Length > 1 && line.Contains('|', StringComparison.Ordinal);

    private static bool IsSeparator(string line) =>
        IsTableRow(line) && Cells(line) is { Count: > 0 } cells && cells.All(c => c.Trim(':').Length > 0 && c.Trim(':').All(ch => ch == '-'));

    private static List<string> Cells(string line)
    {
        var inner = line.Trim();
        if (inner.StartsWith('|'))
        {
            inner = inner[1..];
        }

        if (inner.EndsWith('|') && !inner.EndsWith("\\|", StringComparison.Ordinal))
        {
            inner = inner[..^1];
        }

        var cells = new List<string>();
        var cell = new StringBuilder();
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length && inner[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (inner[i] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(inner[i]);
            }
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }
}
