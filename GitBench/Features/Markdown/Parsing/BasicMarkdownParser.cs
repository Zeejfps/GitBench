using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace GitBench.Features.Markdown.Parsing;

/// <summary>
/// Hand-rolled line-based block scanner for the GFM-flavored subset (see
/// docs/plans/markdown-renderer.md). Step 1 covers block structure only: inline content is kept
/// as a single unstyled <see cref="InlineRun"/> of raw text until the inline parser lands.
///
/// The scanner walks the input line by line, dispatching each line to the block construct it
/// opens; anything unrecognized accumulates into a paragraph, so malformed syntax degrades to
/// literal text and any input prefix parses without throwing (streaming re-parses every delta).
/// Containers (quotes, list items) strip their markers and recurse on the inner lines.
/// </summary>
internal sealed class BasicMarkdownParser : IMarkdownParser
{
    public MarkdownDocument Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) return new MarkdownDocument(Array.Empty<MarkdownBlock>());
        return new MarkdownDocument(ParseBlocks(SplitLines(text)));
    }

    // CRLF normalizes to LF; a trailing newline terminates the last line rather than opening an
    // empty one (an unterminated fence's text must not grow a phantom blank line).
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[start..end]);
            start = i + 1;
        }
        if (start < text.Length)
        {
            var tail = text[start..];
            if (tail.EndsWith('\r')) tail = tail[..^1];
            lines.Add(tail);
        }
        return lines;
    }

    private static IReadOnlyList<MarkdownBlock> ParseBlocks(IReadOnlyList<string> lines)
    {
        var blocks = new List<MarkdownBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
            }
            // Break before list so "- - -" reads as a rule, not a one-item list.
            else if (IsThematicBreak(line))
            {
                blocks.Add(new ThematicBreakBlock());
                i++;
            }
            else if (TryParseHeading(line, out var heading))
            {
                blocks.Add(heading);
                i++;
            }
            else if (TryParseFenceOpener(line, out _, out _, out _))
            {
                blocks.Add(ParseFence(lines, ref i));
            }
            else if (IsQuoteLine(line))
            {
                blocks.Add(ParseQuote(lines, ref i));
            }
            else if (TryParseListMarker(line, out _))
            {
                blocks.Add(ParseList(lines, ref i));
            }
            else if (IsTableStart(lines, i))
            {
                blocks.Add(ParseTable(lines, ref i));
            }
            else
            {
                blocks.Add(ParseParagraph(lines, ref i));
            }
        }
        return blocks;
    }

    private static IReadOnlyList<InlineRun> SingleRun(string text) => new[] { new InlineRun(text) };

    // ------------------------------------------------------------------ paragraphs

    private static ParagraphBlock ParseParagraph(IReadOnlyList<string> lines, ref int i)
    {
        // Leading whitespace is insignificant (indented code blocks are out of scope); trailing
        // spaces survive for Step 2's hard-break detection.
        var text = new StringBuilder(lines[i].TrimStart());
        i++;
        while (i < lines.Count && !StartsNewBlock(lines, i))
        {
            text.Append('\n').Append(lines[i].TrimStart());
            i++;
        }
        return new ParagraphBlock(SingleRun(text.ToString()));
    }

    private static bool StartsNewBlock(IReadOnlyList<string> lines, int i)
    {
        var line = lines[i];
        return string.IsNullOrWhiteSpace(line)
               || IsThematicBreak(line)
               || TryParseHeading(line, out _)
               || TryParseFenceOpener(line, out _, out _, out _)
               || IsQuoteLine(line)
               || TryParseListMarker(line, out _)
               || IsTableStart(lines, i);
    }

    // -------------------------------------------------------------------- headings

    private static bool TryParseHeading(string line, [NotNullWhen(true)] out HeadingBlock? heading)
    {
        heading = null;
        var t = line.TrimStart();
        var level = 0;
        while (level < t.Length && t[level] == '#') level++;
        if (level is < 1 or > 6) return false;
        if (level < t.Length && t[level] is not (' ' or '\t')) return false;

        var text = t[level..].Trim();
        // A trailing #-run is decoration only when a space precedes it: "# title ##" -> "title",
        // but "# title#" keeps its hash.
        var end = text.Length;
        while (end > 0 && text[end - 1] == '#') end--;
        if (end < text.Length && end > 0 && text[end - 1] == ' ')
        {
            text = text[..end].TrimEnd();
        }
        heading = new HeadingBlock(level, SingleRun(text));
        return true;
    }

    // --------------------------------------------------------------- thematic breaks

    private static bool IsThematicBreak(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || t[0] is not ('-' or '*' or '_')) return false;
        var marker = t[0];
        var count = 0;
        foreach (var c in t)
        {
            if (c == marker) count++;
            else if (c is not (' ' or '\t')) return false;
        }
        return count >= 3;
    }

    // ----------------------------------------------------------------- code fences

    private static readonly char[] InfoStringSeparators = { ' ', '\t' };

    private static bool TryParseFenceOpener(string line, out char fenceChar, out int fenceLength, out string? language)
    {
        fenceChar = default;
        fenceLength = 0;
        language = null;
        var t = line.TrimStart();
        if (t.Length == 0 || t[0] is not ('`' or '~')) return false;
        var c = t[0];
        var length = 0;
        while (length < t.Length && t[length] == c) length++;
        if (length < 3) return false;

        var info = t[length..].Trim();
        if (info.Length > 0)
        {
            var separator = info.IndexOfAny(InfoStringSeparators);
            language = separator < 0 ? info : info[..separator];
        }
        fenceChar = c;
        fenceLength = length;
        return true;
    }

    private static CodeBlock ParseFence(IReadOnlyList<string> lines, ref int i)
    {
        TryParseFenceOpener(lines[i], out var fenceChar, out var fenceLength, out var language);
        i++;
        var content = new List<string>();
        var closed = false;
        while (i < lines.Count)
        {
            if (IsClosingFence(lines[i], fenceChar, fenceLength))
            {
                closed = true;
                i++;
                break;
            }
            content.Add(lines[i]);
            i++;
        }
        return new CodeBlock(language, string.Join("\n", content), closed);
    }

    // Closing fence: a run of the opener's char at least as long as the opener, nothing else.
    // Shorter runs and the other fence char are content.
    private static bool IsClosingFence(string line, char fenceChar, int openerLength)
    {
        var t = line.Trim();
        if (t.Length < openerLength) return false;
        foreach (var c in t)
        {
            if (c != fenceChar) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ blockquotes

    private static bool IsQuoteLine(string line)
    {
        var t = line.TrimStart();
        return t.Length > 0 && t[0] == '>';
    }

    // No lazy continuation in this subset: the quote spans exactly the run of >-prefixed lines.
    private static QuoteBlock ParseQuote(IReadOnlyList<string> lines, ref int i)
    {
        var inner = new List<string>();
        while (i < lines.Count && IsQuoteLine(lines[i]))
        {
            var t = lines[i].TrimStart()[1..];
            if (t.StartsWith(' ')) t = t[1..];
            inner.Add(t);
            i++;
        }
        return new QuoteBlock(ParseBlocks(inner));
    }

    // ------------------------------------------------------------------------ lists

    /// <summary>
    /// A recognized list-item marker line. <paramref name="ContentIndent"/> is the column where
    /// the item's text starts — nested lines strip up to that many leading columns before the
    /// item's blocks re-parse.
    /// </summary>
    private readonly record struct ListMarker(int Indent, int ContentIndent, bool Ordered, int Number, string Content);

    private static int LeadingWhitespace(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] is ' ' or '\t') count++;
        return count;
    }

    private static bool TryParseListMarker(string line, out ListMarker marker)
    {
        marker = default;
        var indent = LeadingWhitespace(line);
        var pos = indent;
        if (pos >= line.Length) return false;
        var c = line[pos];
        if (c is '-' or '*' or '+')
        {
            if (pos + 1 >= line.Length || line[pos + 1] is not (' ' or '\t')) return false;
            marker = new ListMarker(indent, pos + 2, Ordered: false, Number: 1, line[(pos + 2)..]);
            return true;
        }
        if (char.IsAsciiDigit(c))
        {
            var end = pos;
            while (end < line.Length && char.IsAsciiDigit(line[end])) end++;
            if (end - pos > 9) return false;
            if (end >= line.Length || line[end] is not ('.' or ')')) return false;
            if (end + 1 >= line.Length || line[end + 1] is not (' ' or '\t')) return false;
            if (!int.TryParse(line.AsSpan(pos, end - pos), out var number)) return false;
            marker = new ListMarker(indent, end + 2, Ordered: true, number, line[(end + 2)..]);
            return true;
        }
        return false;
    }

    private static ListBlock ParseList(IReadOnlyList<string> lines, ref int i)
    {
        TryParseListMarker(lines[i], out var first);
        var baseIndent = first.Indent;
        var items = new List<ListItem>();

        // A marker of the other type (ordered vs. unordered) at this indent starts a new list.
        while (i < lines.Count
               && TryParseListMarker(lines[i], out var marker)
               && marker.Indent <= baseIndent
               && marker.Ordered == first.Ordered)
        {
            i++;
            var content = marker.Content;
            var task = TryStripTaskMarker(ref content);
            var itemLines = new List<string> { content };

            // Deeper-indented lines belong to this item: plain text continues its paragraph,
            // marker lines become a nested list — both fall out of re-parsing the collected
            // lines. A blank line only ends the list when unindented text follows.
            var pendingBlanks = 0;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    pendingBlanks++;
                    i++;
                    continue;
                }
                var indent = LeadingWhitespace(line);
                if (indent <= baseIndent) break;
                for (; pendingBlanks > 0; pendingBlanks--)
                {
                    itemLines.Add(string.Empty);
                }
                itemLines.Add(line[Math.Min(marker.ContentIndent, indent)..]);
                i++;
            }
            items.Add(new ListItem(ParseBlocks(itemLines), task));
        }

        return new ListBlock(first.Ordered, first.Ordered ? first.Number : 1, items);
    }

    // GFM task marker: "[ ] ", "[x] ", or "[X] " at the very start of the item's content.
    // Anything else (including a bracket pair with no trailing space) stays literal text.
    private static bool? TryStripTaskMarker(ref string content)
    {
        if (content.Length < 4 || content[0] != '[' || content[2] != ']' || content[3] != ' ') return null;
        var state = content[1];
        if (state is not (' ' or 'x' or 'X')) return null;
        content = content[4..];
        return state != ' ';
    }

    // ----------------------------------------------------------------------- tables

    // A table needs a header row with a pipe and a delimiter row with the same column count;
    // anything less stays a paragraph.
    private static bool IsTableStart(IReadOnlyList<string> lines, int i)
    {
        if (i + 1 >= lines.Count) return false;
        if (!ContainsUnescapedPipe(lines[i])) return false;
        if (!TryParseDelimiterRow(lines[i + 1], out var alignments)) return false;
        return alignments.Count > 0 && SplitTableCells(lines[i]).Count == alignments.Count;
    }

    private static TableBlock ParseTable(IReadOnlyList<string> lines, ref int i)
    {
        TryParseDelimiterRow(lines[i + 1], out var alignments);
        var header = ParseRowCells(lines[i], alignments.Count);
        i += 2;

        var rows = new List<IReadOnlyList<IReadOnlyList<InlineRun>>>();
        while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]) && ContainsUnescapedPipe(lines[i]))
        {
            rows.Add(ParseRowCells(lines[i], alignments.Count));
            i++;
        }
        return new TableBlock(alignments, header, rows);
    }

    // Short rows pad with empty cells, long rows truncate: every row matches the header width.
    private static IReadOnlyList<IReadOnlyList<InlineRun>> ParseRowCells(string line, int columnCount)
    {
        var raw = SplitTableCells(line);
        var cells = new List<IReadOnlyList<InlineRun>>(columnCount);
        for (var c = 0; c < columnCount; c++)
        {
            var text = c < raw.Count ? raw[c].Trim() : string.Empty;
            cells.Add(text.Length == 0 ? Array.Empty<InlineRun>() : SingleRun(text));
        }
        return cells;
    }

    private static bool ContainsUnescapedPipe(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') i++;
            else if (line[i] == '|') return true;
        }
        return false;
    }

    // Splits on unescaped pipes ("\|" is a literal), dropping the optional outer border cells.
    // Cells come back untrimmed; callers trim per use (alignment cells vs. content cells).
    private static List<string> SplitTableCells(string line)
    {
        var t = line.Trim();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var endsWithBorderPipe = false;
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (c == '|')
            {
                cells.Add(cell.ToString());
                cell.Clear();
                endsWithBorderPipe = i == t.Length - 1;
            }
            else
            {
                cell.Append(c);
            }
        }
        cells.Add(cell.ToString());
        if (t.Length > 0 && t[0] == '|') cells.RemoveAt(0);
        if (endsWithBorderPipe && cells.Count > 0) cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    // Delimiter cell grammar: optional ':', one or more '-', optional ':'.
    private static bool TryParseDelimiterRow(string line, out List<ColumnAlignment> alignments)
    {
        alignments = new List<ColumnAlignment>();
        foreach (var rawCell in SplitTableCells(line))
        {
            var cell = rawCell.Trim();
            if (cell.Length == 0) return false;
            var start = 0;
            var end = cell.Length;
            var left = cell[0] == ':';
            if (left) start++;
            var right = end > start && cell[end - 1] == ':';
            if (right) end--;
            if (end <= start) return false;
            for (var i = start; i < end; i++)
            {
                if (cell[i] != '-') return false;
            }
            alignments.Add(
                left && right ? ColumnAlignment.Center :
                left ? ColumnAlignment.Left :
                right ? ColumnAlignment.Right : ColumnAlignment.None);
        }
        return alignments.Count > 0;
    }
}
