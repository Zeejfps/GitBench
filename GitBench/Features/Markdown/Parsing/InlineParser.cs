using System.Text;

namespace GitBench.Features.Markdown.Parsing;

/// <summary>
/// Step 2's inline resolver: takes the raw inline text of one paragraph, heading, or table cell
/// and produces the flat, pre-resolved <see cref="InlineRun"/> list the renderer consumes.
/// Covers the scoped subset (docs/plans/markdown-renderer.md): emphasis (<c>*</c>/<c>**</c>/
/// <c>***</c>/<c>_</c>), inline code (backtick runs, code wins over emphasis), strikethrough,
/// links, bare-URL autolinks, backslash escapes, and hard breaks. Nesting resolves into style
/// flags on flat runs; adjacent runs with identical styling merge; unmatched delimiters degrade
/// to literal text. Never throws.
///
/// Shape: a single left-to-right scan turns the text into a node list — literal text, resolved
/// atoms (code spans, links, autolinks), hard breaks, '[' openers, and emphasis delimiter runs.
/// Precedence falls out of the scan order: code spans consume their content immediately, links
/// collapse their bracketed slice into an atom the moment they close (resolving the emphasis
/// inside it), and a trimmed CommonMark delimiter-stack pass resolves whatever delimiters remain
/// before the node list flattens into merged runs.
/// </summary>
internal static class InlineParser
{
    /// <summary>Resolves <paramref name="text"/> into flat styled runs. Never throws.</summary>
    internal static IReadOnlyList<InlineRun> Parse(string text)
    {
        var nodes = Scan(text);
        ResolveEmphasis(nodes, 0);
        return Flatten(nodes, 0);
    }

    // --------------------------------------------------------------------- scan

    private static List<Node> Scan(string s)
    {
        var nodes = new List<Node>();
        var text = new StringBuilder();

        void Flush()
        {
            if (text.Length == 0) return;
            nodes.Add(new TextNode(text.ToString()));
            text.Clear();
        }

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            switch (c)
            {
                case '\\':
                    // Escapes cover ASCII punctuation; before anything else the backslash stays.
                    if (i + 1 < s.Length && IsEscapable(s[i + 1]))
                    {
                        text.Append(s[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        text.Append('\\');
                        i++;
                    }
                    break;

                case '`':
                {
                    var open = RunLength(s, i, '`');
                    if (TryFindCodeSpanClose(s, i + open, open, out var contentEnd, out var next))
                    {
                        Flush();
                        var content = CodeSpanContent(s, i + open, contentEnd);
                        nodes.Add(new AtomNode(new[] { new InlineRun(content, Code: true) }));
                        i = next;
                    }
                    else
                    {
                        text.Append('`', open);
                        i += open;
                    }
                    break;
                }

                case '[':
                    Flush();
                    nodes.Add(new BracketNode());
                    i++;
                    break;

                case ']':
                    Flush();
                    if (!TryCloseLink(s, nodes, ref i))
                    {
                        text.Append(']');
                        i++;
                    }
                    break;

                case '*' or '_' or '~':
                {
                    var count = RunLength(s, i, c);
                    if (c == '~' && count < 2)
                    {
                        // Single tildes are not strikethrough.
                        text.Append('~');
                        i++;
                        break;
                    }
                    var prev = i > 0 ? s[i - 1] : ' ';
                    var following = i + count < s.Length ? s[i + count] : ' ';
                    // Flanking: whitespace on the inner side blocks that direction outright...
                    var canOpen = !char.IsWhiteSpace(following);
                    var canClose = !char.IsWhiteSpace(prev);
                    if (c == '_')
                    {
                        // ...and underscores additionally refuse intraword emphasis, so
                        // snake_case stays literal while a*b*c still works.
                        canOpen = canOpen && !char.IsLetterOrDigit(prev);
                        canClose = canClose && !char.IsLetterOrDigit(following);
                    }
                    Flush();
                    nodes.Add(new DelimiterNode(c, count, canOpen, canClose));
                    i += count;
                    break;
                }

                case ' ':
                {
                    var count = RunLength(s, i, ' ');
                    if (count >= 2 && i + count < s.Length && s[i + count] == '\n')
                    {
                        // Hard break: the break-forming spaces and the newline are consumed.
                        // Trailing spaces at end of input (no newline) fall through as literal.
                        Flush();
                        nodes.Add(new BreakNode());
                        i += count + 1;
                    }
                    else
                    {
                        text.Append(' ', count);
                        i += count;
                    }
                    break;
                }

                case 'h':
                    if ((i == 0 || !char.IsLetterOrDigit(s[i - 1])) && TryMatchAutolink(s, i, out var urlEnd))
                    {
                        Flush();
                        var url = s[i..urlEnd];
                        nodes.Add(new AtomNode(new[] { new InlineRun(url, LinkUrl: url) }));
                        i = urlEnd;
                    }
                    else
                    {
                        text.Append('h');
                        i++;
                    }
                    break;

                default:
                    text.Append(c);
                    i++;
                    break;
            }
        }
        Flush();
        return nodes;
    }

    // -------------------------------------------------------------------- links

    // Called with i at ']'. On success the bracketed slice collapses into one linked atom and i
    // moves past "(url)"; on a failed "(url)" the matched opener is spent — a later ']' cannot
    // reuse it — and the ']' stays literal.
    private static bool TryCloseLink(string s, List<Node> nodes, ref int i)
    {
        BracketNode? opener = null;
        var oi = -1;
        for (var k = nodes.Count - 1; k >= 0; k--)
        {
            if (nodes[k] is BracketNode { Active: true } bracket)
            {
                opener = bracket;
                oi = k;
                break;
            }
        }
        if (opener is null) return false;

        // This subset requires "(url)" immediately after "]"; the URL runs verbatim to the
        // first ')' and is never inline-parsed or styled.
        var close = i + 1 < s.Length && s[i + 1] == '(' ? s.IndexOf(')', i + 2) : -1;
        if (close < 0)
        {
            opener.Active = false;
            return false;
        }
        var url = s[(i + 2)..close];

        // The link text resolves its own emphasis in isolation — delimiters inside never pair
        // with delimiters outside the brackets.
        ResolveEmphasis(nodes, oi + 1);
        var inner = Flatten(nodes, oi + 1);
        var linked = new List<InlineRun>(inner.Count);
        foreach (var run in inner)
        {
            // Hard breaks stay unstyled and unlinked; everything else takes the URL. Runs that
            // now agree on every style (e.g. an autolink that only differed by URL) merge later,
            // when the consuming Flatten's Emit walks this atom.
            linked.Add(IsHardBreak(run) ? run : run with { LinkUrl = url });
        }

        nodes.RemoveRange(oi, nodes.Count - oi);
        nodes.Add(new AtomNode(linked));
        // No nested links — the inner link wins: forming this one spends every enclosing '[',
        // so an outer pair can never become a link and its brackets stay literal.
        foreach (var node in nodes)
        {
            if (node is BracketNode remaining) remaining.Active = false;
        }
        i = close + 1;
        return true;
    }

    // ---------------------------------------------------------------- autolinks

    private static bool TryMatchAutolink(string s, int i, out int end)
    {
        end = 0;
        int schemeEnd;
        if (s.AsSpan(i).StartsWith("https://", StringComparison.Ordinal)) schemeEnd = i + 8;
        else if (s.AsSpan(i).StartsWith("http://", StringComparison.Ordinal)) schemeEnd = i + 7;
        else return false;

        var stop = schemeEnd;
        while (stop < s.Length && !char.IsWhiteSpace(s[stop])) stop++;
        // Repeated trailing-punctuation trim, no paren balancing. Emphasis/strike markers trim
        // too; trimmed characters are re-scanned rather than consumed, so the closer in
        // "**https://e.com**" still pairs with its opener.
        while (stop > schemeEnd && IsAutolinkTrailing(s[stop - 1])) stop--;
        if (stop == schemeEnd) return false;
        end = stop;
        return true;
    }

    private static bool IsAutolinkTrailing(char c)
        => c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or '*' or '_' or '~';

    // --------------------------------------------------------------- code spans

    // A backtick run closes at the next run of exactly the same length; longer or shorter runs
    // are content.
    private static bool TryFindCodeSpanClose(string s, int from, int openLength, out int contentEnd, out int next)
    {
        var k = from;
        while (k < s.Length)
        {
            if (s[k] != '`')
            {
                k++;
                continue;
            }
            var run = RunLength(s, k, '`');
            if (run == openLength)
            {
                contentEnd = k;
                next = k + run;
                return true;
            }
            k += run;
        }
        contentEnd = 0;
        next = 0;
        return false;
    }

    // One flanking space pair strips when both sides have one and the content is not all
    // spaces; everything inside is verbatim (no escapes, no nested markup).
    private static string CodeSpanContent(string s, int start, int end)
    {
        if (end - start >= 2 && s[start] == ' ' && s[end - 1] == ' ')
        {
            for (var k = start; k < end; k++)
            {
                if (s[k] != ' ') return s[(start + 1)..(end - 1)];
            }
        }
        return s[start..end];
    }

    // ----------------------------------------------------------------- emphasis

    // CommonMark's delimiter-stack process trimmed to the subset: walk closers left to right,
    // pair each against the nearest eligible opener at or after the stack bottom, and spend
    // delimiters stranded between a matched pair (nothing can pair across it). Matches become
    // open/close style counts on the nodes; leftovers stay literal marker text.
    private static void ResolveEmphasis(List<Node> nodes, int from)
    {
        for (var ci = from; ci < nodes.Count; ci++)
        {
            if (nodes[ci] is not DelimiterNode closer || !closer.CanClose) continue;
            // Strikethrough only ever pairs two tildes at a time; emphasis pairs one or two.
            var unit = closer.Marker == '~' ? 2 : 1;
            while (closer.Count >= unit)
            {
                var oi = -1;
                for (var k = ci - 1; k >= from; k--)
                {
                    if (nodes[k] is DelimiterNode d && d.Marker == closer.Marker && d.CanOpen && d.Count >= unit)
                    {
                        oi = k;
                        break;
                    }
                }
                if (oi < 0) break;
                var opener = (DelimiterNode)nodes[oi];

                if (closer.Marker == '~')
                {
                    opener.OpensStrike++;
                    closer.ClosesStrike++;
                    opener.Count -= 2;
                    closer.Count -= 2;
                }
                else if (opener.Count >= 2 && closer.Count >= 2)
                {
                    // Two from each side make bold; a triple run spends its third char on a
                    // second lap through this loop, which is how *** becomes bold italic.
                    opener.OpensBold++;
                    closer.ClosesBold++;
                    opener.Count -= 2;
                    closer.Count -= 2;
                }
                else
                {
                    opener.OpensItalic++;
                    closer.ClosesItalic++;
                    opener.Count -= 1;
                    closer.Count -= 1;
                }

                for (var k = oi + 1; k < ci; k++)
                {
                    if (nodes[k] is DelimiterNode stranded)
                    {
                        stranded.CanOpen = false;
                        stranded.CanClose = false;
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ flatten

    private static List<InlineRun> Flatten(List<Node> nodes, int from)
    {
        var runs = new List<InlineRun>();
        var bold = 0;
        var italic = 0;
        var strike = 0;
        var lastIsBreak = false;

        void Emit(string t, bool b, bool it, bool code, bool st, string? url)
        {
            if (t.Length == 0) return;
            if (!lastIsBreak && runs.Count > 0)
            {
                var last = runs[^1];
                if (last.Bold == b && last.Italic == it && last.Code == code
                    && last.Strikethrough == st && last.LinkUrl == url)
                {
                    runs[^1] = last with { Text = last.Text + t };
                    return;
                }
            }
            runs.Add(new InlineRun(t, b, it, code, st, url));
            lastIsBreak = false;
        }

        for (var idx = from; idx < nodes.Count; idx++)
        {
            switch (nodes[idx])
            {
                case TextNode t:
                    Emit(t.Text, bold > 0, italic > 0, false, strike > 0, null);
                    break;

                case AtomNode atom:
                    foreach (var r in atom.Runs)
                    {
                        if (IsHardBreak(r))
                        {
                            runs.Add(r);
                            lastIsBreak = true;
                        }
                        else
                        {
                            // Surrounding emphasis ORs onto the atom's own styling.
                            Emit(r.Text, r.Bold || bold > 0, r.Italic || italic > 0, r.Code,
                                r.Strikethrough || strike > 0, r.LinkUrl);
                        }
                    }
                    break;

                case BreakNode:
                    // The pinned hard-break shape: a lone "\n" run, never styled, never merged.
                    runs.Add(new InlineRun("\n"));
                    lastIsBreak = true;
                    break;

                case BracketNode:
                    Emit("[", bold > 0, italic > 0, false, strike > 0, null);
                    break;

                case DelimiterNode d:
                    // Closes, then unmatched leftovers as literal marker text, then opens — a
                    // closer's leftover lands outside the emphasis, an opener's before it.
                    bold -= d.ClosesBold;
                    italic -= d.ClosesItalic;
                    strike -= d.ClosesStrike;
                    if (d.Count > 0)
                    {
                        Emit(new string(d.Marker, d.Count), bold > 0, italic > 0, false, strike > 0, null);
                    }
                    bold += d.OpensBold;
                    italic += d.OpensItalic;
                    strike += d.OpensStrike;
                    break;
            }
        }
        return runs;
    }

    // ------------------------------------------------------------------ helpers

    private static int RunLength(string s, int i, char c)
    {
        var n = 0;
        while (i + n < s.Length && s[i + n] == c) n++;
        return n;
    }

    // CommonMark's escapable set: ASCII punctuation.
    private static bool IsEscapable(char c)
        => c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');

    private static bool IsHardBreak(InlineRun r)
        => r is { Text: "\n", Bold: false, Italic: false, Code: false, Strikethrough: false, LinkUrl: null };

    // -------------------------------------------------------------------- nodes

    private abstract class Node
    {
    }

    /// <summary>Literal text; styled by whatever emphasis is open around it.</summary>
    private sealed class TextNode : Node
    {
        public readonly string Text;

        public TextNode(string text) => Text = text;
    }

    /// <summary>
    /// A fully resolved construct (code span, link, autolink): its runs are final except that
    /// surrounding emphasis still ORs its flags onto them.
    /// </summary>
    private sealed class AtomNode : Node
    {
        public readonly IReadOnlyList<InlineRun> Runs;

        public AtomNode(IReadOnlyList<InlineRun> runs) => Runs = runs;
    }

    /// <summary>A hard break; flattens to the dedicated unstyled "\n" run.</summary>
    private sealed class BreakNode : Node
    {
    }

    /// <summary>A '[' that may yet open a link; flattens to a literal "[" when it never does.</summary>
    private sealed class BracketNode : Node
    {
        public bool Active = true;
    }

    /// <summary>
    /// An emphasis delimiter run. Matching decrements <see cref="Count"/> and records the styles
    /// this node opens/closes; whatever count survives flattens as literal marker characters.
    /// </summary>
    private sealed class DelimiterNode : Node
    {
        public readonly char Marker;
        public int Count;
        public bool CanOpen;
        public bool CanClose;
        public int OpensBold, OpensItalic, OpensStrike;
        public int ClosesBold, ClosesItalic, ClosesStrike;

        public DelimiterNode(char marker, int count, bool canOpen, bool canClose)
        {
            Marker = marker;
            Count = count;
            CanOpen = canOpen;
            CanClose = canClose;
        }
    }
}
