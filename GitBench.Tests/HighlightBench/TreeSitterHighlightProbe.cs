using System.Text;

using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Theming;

using TreeSitter;

namespace GitBench.Tests.HighlightBench;

/// <summary>
/// A tree-sitter implementation of the highlighting seam, built to be measured against
/// <see cref="SyntaxHighlighter"/> rather than shipped: it holds one parser, compiles the
/// upstream <c>highlights.scm</c> once, and reports where its time went.
/// </summary>
internal sealed class TreeSitterHighlightProbe : IDisposable
{
    private readonly Parser _parser;
    private readonly Query _query;
    private readonly QueryCursor _cursor;
    private readonly TokenColorSlot[] _slotOfCapture;

    private TreeSitterHighlightProbe(Language grammar, Query query, int patternsKept, int patternsTotal)
    {
        _parser = new Parser(grammar);
        _cursor = new QueryCursor();
        _query = query;
        PatternsKept = patternsKept;
        PatternsTotal = patternsTotal;

        _slotOfCapture = new TokenColorSlot[query.CaptureCount];
        for (var id = 0u; id < query.CaptureCount; id++)
        {
            _slotOfCapture[id] = HighlightCaptureMap.Map(query.CaptureName(id));
        }
    }

    public int PatternsKept { get; }

    public int PatternsTotal { get; }

    /// <summary>
    /// Compiles <paramref name="queryText"/> against the bundled grammar for
    /// <paramref name="language"/>, dropping only the individual patterns tree-sitter rejects.
    /// </summary>
    /// <remarks>
    /// Whole-query compilation is tried first and is the normal path. The per-pattern fallback
    /// exists because the upstream query files are written for the <c>tree-sitter-highlight</c>
    /// crate and use two things this host does not: <c>#is-not? local</c>, and (for TypeScript and
    /// TSX) a JavaScript query concatenated onto a grammar whose node set is close but not
    /// identical. How many patterns survive is itself a measurement, so it is reported rather than
    /// hidden.
    /// </remarks>
    public static TreeSitterHighlightProbe Create(CodeLanguage language, string queryText)
    {
        var grammar = Language.Load("tree-sitter-grammars", language.GrammarName());
        var patterns = SplitPatterns(queryText);

        try
        {
            var whole = Query.Compile(grammar, queryText);
            return new TreeSitterHighlightProbe(grammar, whole, patterns.Count, patterns.Count);
        }
        catch (Exception)
        {
            // fall through to per-pattern compilation
        }

        var kept = new StringBuilder();
        var keptCount = 0;
        foreach (var pattern in patterns)
        {
            try
            {
                using var probe = Query.Compile(grammar, pattern);
            }
            catch (Exception)
            {
                continue;
            }

            kept.Append(pattern).Append('\n');
            keptCount++;
        }

        if (keptCount == 0)
        {
            throw new InvalidOperationException($"No pattern of the {language} highlights query compiled.");
        }

        var query = Query.Compile(grammar, kept.ToString());
        return new TreeSitterHighlightProbe(grammar, query, keptCount, patterns.Count);
    }

    /// <summary>Tokenizes one file, reporting per-phase cost alongside the spans.</summary>
    public HighlightRun Highlight(string fileText)
    {
        var normalized = fileText.Contains('\r')
            ? fileText.Replace("\r\n", "\n").Replace('\r', '\n')
            : fileText;
        var utf8 = Encoding.UTF8.GetBytes(normalized);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var tree = _parser.Parse(utf8);
        var parsed = watch.Elapsed;

        var captures = new List<Capture>();
        _cursor.ForEachMatch(_query, tree.RootNode, match =>
        {
            for (var i = 0; i < match.CaptureCount; i++)
            {
                var slot = _slotOfCapture[match.CaptureIdAt(i)];
                if (slot == TokenColorSlot.Default) continue;
                var node = match.NodeAt(i);
                if (node.EndByte <= node.StartByte) continue;
                captures.Add(new Capture(node.StartByte, node.EndByte, match.PatternIndex, slot));
            }
        });
        var queried = watch.Elapsed;

        var spans = BuildSpans(normalized, utf8, captures);
        var built = watch.Elapsed;

        return new HighlightRun(spans, parsed, queried - parsed, built - queried, tree.RootNode.HasError);
    }

    public void Dispose()
    {
        _cursor.Dispose();
        _query.Dispose();
        _parser.Dispose();
    }

    /// <summary>
    /// Paints captures onto a per-character slot map, then coalesces each line's runs.
    /// </summary>
    /// <remarks>
    /// Outer-then-inner painting is the precedence rule: sorting by start ascending and end
    /// descending puts the larger span down first, so a nested capture overwrites the construct
    /// containing it, and two captures over the identical range resolve to whichever pattern is
    /// written later in the query file. The tie-break is the pattern index and not the order
    /// matches arrive in — every one of these query files opens with a catch-all
    /// <c>(identifier) @variable</c> that later patterns are meant to override, and tree-sitter
    /// does not order same-position matches by pattern.
    /// </remarks>
    private static IReadOnlyList<IReadOnlyList<TokenSpan>> BuildSpans(
        string text,
        byte[] utf8,
        List<Capture> captures)
    {
        captures.Sort(static (a, b) =>
        {
            var byStart = a.StartByte.CompareTo(b.StartByte);
            if (byStart != 0) return byStart;
            var byEnd = b.EndByte.CompareTo(a.EndByte);
            return byEnd != 0 ? byEnd : a.PatternIndex.CompareTo(b.PatternIndex);
        });

        var charOfByte = utf8.Length == text.Length ? null : ByteToCharMap(text, utf8.Length);
        var slots = new byte[text.Length];

        foreach (var capture in captures)
        {
            var start = charOfByte is null ? (int)capture.StartByte : charOfByte[capture.StartByte];
            var end = charOfByte is null ? (int)capture.EndByte : charOfByte[capture.EndByte];
            if (end > text.Length) end = text.Length;
            for (var i = start; i < end; i++) slots[i] = (byte)capture.Slot;
        }

        var lines = new List<IReadOnlyList<TokenSpan>>();
        var lineStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n') continue;
            lines.Add(LineSpans(text, slots, lineStart, i));
            lineStart = i + 1;
        }

        return lines;
    }

    private static IReadOnlyList<TokenSpan> LineSpans(string text, byte[] slots, int start, int end)
    {
        List<TokenSpan>? spans = null;
        var column = 0;
        var runSlot = (byte)TokenColorSlot.Default;
        var runStart = 0;

        for (var i = start; i < end; i++)
        {
            var slot = slots[i];
            if (slot != runSlot)
            {
                if (runSlot != (byte)TokenColorSlot.Default)
                {
                    (spans ??= []).Add(new TokenSpan(runStart, column - runStart, (TokenColorSlot)runSlot));
                }

                runSlot = slot;
                runStart = column;
            }

            column += text[i] == '\t' ? DiffOptions.TabWidth : 1;
        }

        if (runSlot != (byte)TokenColorSlot.Default)
        {
            (spans ??= []).Add(new TokenSpan(runStart, column - runStart, (TokenColorSlot)runSlot));
        }

        return (IReadOnlyList<TokenSpan>?)spans ?? [];
    }

    private static int[] ByteToCharMap(string text, int byteCount)
    {
        var map = new int[byteCount + 1];
        var bi = 0;
        var ci = 0;

        while (ci < text.Length && bi < byteCount)
        {
            var wide = char.IsHighSurrogate(text[ci]) && ci + 1 < text.Length && char.IsLowSurrogate(text[ci + 1]);
            var codepoint = wide ? char.ConvertToUtf32(text[ci], text[ci + 1]) : text[ci];
            var width = codepoint < 0x80 ? 1 : codepoint < 0x800 ? 2 : codepoint < 0x10000 ? 3 : 4;

            for (var k = 0; k < width && bi + k < byteCount; k++) map[bi + k] = ci;

            bi += width;
            ci += wide ? 2 : 1;
        }

        map[byteCount] = text.Length;
        return map;
    }

    /// <summary>Splits a query source into its top-level patterns, comments and strings aware.</summary>
    private static List<string> SplitPatterns(string source)
    {
        var patterns = new List<string>();
        var depth = 0;
        var start = -1;

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];

            if (c == ';' && depth == 0)
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            if (c == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
                continue;
            }

            if (c is '(' or '[')
            {
                if (depth == 0) start = i;
                depth++;
                continue;
            }

            if (c is ')' or ']')
            {
                depth--;
                if (depth != 0 || start < 0) continue;

                // A trailing "@capture" (and any predicate that follows) belongs to the pattern
                // that just closed, so run to the end of its line rather than to the paren.
                var end = i + 1;
                while (end < source.Length && source[end] != '\n') end++;
                patterns.Add(source[start..end]);
                start = -1;
            }
        }

        return patterns;
    }

    private readonly record struct Capture(uint StartByte, uint EndByte, int PatternIndex, TokenColorSlot Slot);
}

internal readonly record struct HighlightRun(
    IReadOnlyList<IReadOnlyList<TokenSpan>> Spans,
    TimeSpan Parse,
    TimeSpan Query,
    TimeSpan Build,
    bool HasParseError)
{
    public TimeSpan Total => Parse + Query + Build;
}
