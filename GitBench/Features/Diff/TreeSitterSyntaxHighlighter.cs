using GitBench.Features.CodeIntel;
using GitBench.Theming;

using TreeSitter;

namespace GitBench.Features.Diff;

/// <summary>
/// Tokenizes with a real parser: parses the file with its bundled tree-sitter grammar, runs the
/// grammar's own <c>highlights.scm</c> over the tree, and paints the captures into the same
/// per-line <see cref="TokenSpan"/> lists <see cref="SyntaxHighlighter"/> produces.
///
/// A region a grammar hands to another language — a fenced code block, a <c>&lt;script&gt;</c>
/// body, Markdown's inline syntax — is parsed again with that language and painted over the top,
/// which is what a file made of several languages needs and what regexes approximate.
/// </summary>
/// <remarks>
/// Safe to call concurrently, and worth calling concurrently: a parser is per-worker by
/// construction, so unlike the TextMate engine this has no lock for a per-file lane to queue on.
/// </remarks>
internal sealed class TreeSitterSyntaxHighlighter
{
    private const int MaxInjectionDepth = 3;

    private readonly TreeSitterGrammars _grammars;
    private readonly Action<string>? _log;
    private int _failureLogged;

    public TreeSitterSyntaxHighlighter(TreeSitterGrammars grammars, Action<string>? log = null)
    {
        _grammars = grammars;
        _log = log;
    }

    /// <summary>Whether this engine, and not TextMate, should color a language.</summary>
    public bool Supports(CodeLanguage language) => _grammars.Get(language)?.Highlights is not null;

    /// <inheritdoc cref="SyntaxHighlighter.Highlight"/>
    public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(fileText);

        if (!Supports(language)) return null;
        if (ParseText.Of(fileText) is not { } input) return null;

        try
        {
            var captures = new List<Capture>();
            Collect(language, input.Utf8, [new Region(0, input.Utf8.Length)], depth: 0, captures);
            return Coalesce(input.Normalized, input.Utf8.Length, captures);
        }
        catch (Exception error)
        {
            LogOnce($"Tree-sitter highlighting failed on a {language} file: {error}");
            return null;
        }
    }

    /// <summary>
    /// Colors a file from a tree already parsed for it — the incremental path, where the tree is
    /// maintained across edits rather than built per call.
    /// </summary>
    /// <remarks>
    /// Injections are re-derived from <paramref name="root"/> rather than carried, which is what the
    /// whole-file path does too: an incremental parse is defined to produce the tree a parse from
    /// scratch would, so what a fresh root says about its injected regions is what a fresh parse
    /// would have said.
    /// </remarks>
    /// <param name="normalized">The file with its line endings already normalized — the text
    /// <paramref name="utf8"/> encodes.</param>
    public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(
        CodeLanguage language, string normalized, byte[] utf8, SyntaxTree root)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentNullException.ThrowIfNull(root);

        if (_grammars.Get(language) is not { Highlights: { } highlights } grammar) return null;
        if (utf8.Length > ParseText.MaxFileBytes) return null;

        try
        {
            var captures = new List<Capture>();
            CollectRoot(grammar, highlights, utf8, root, captures);
            return Coalesce(normalized, utf8.Length, captures);
        }
        catch (Exception error)
        {
            LogOnce($"Tree-sitter highlighting failed on a {language} file: {error}");
            return null;
        }
    }

    // Recursion runs after the session is returned: nesting Use on a pool of one would wait on a
    // slot this caller is holding.
    private void Collect(CodeLanguage language, byte[] utf8, List<Region> regions, int depth, List<Capture> captures)
    {
        if (_grammars.Get(language) is not { Highlights: { } highlights } grammar) return;

        var injected = grammar.Pool.Use(
            (language, highlights, utf8, regions, depth, captures, follow: depth < MaxInjectionDepth),
            static (session, s) =>
            {
                Dictionary<Region, Injection>? found = null;
                foreach (var region in s.regions)
                {
                    Scan(session, s.language, s.highlights, s.utf8, region, s.depth, s.captures, s.follow, ref found);
                }

                return found;
            });

        Follow(injected, utf8, depth, captures);
    }

    // The root region, read off a tree this engine did not parse here and does not own.
    private void CollectRoot(CompiledGrammar grammar, HighlightQuery highlights, byte[] utf8, SyntaxTree tree, List<Capture> captures)
    {
        var injected = grammar.Pool.Use(
            (language: grammar.Language, highlights, tree, captures, region: new Region(0, utf8.Length)),
            static (session, s) =>
            {
                Dictionary<Region, Injection>? found = null;
                ScanTree(
                    session, s.language, s.highlights, s.tree.RootNode, s.region,
                    depth: 0, s.captures, followInjections: true, ref found);
                return found;
            });

        Follow(injected, utf8, depth: 0, captures);
    }

    private void Follow(Dictionary<Region, Injection>? injected, byte[] utf8, int depth, List<Capture> captures)
    {
        if (injected is null) return;

        foreach (var group in injected.Values.GroupBy(i => i.Language))
        {
            Collect(group.Key, utf8, [.. group.Select(i => i.Region)], depth + 1, captures);
        }
    }

    private static void Scan(
        ParseSession session,
        CodeLanguage host,
        HighlightQuery compiled,
        byte[] utf8,
        Region region,
        int depth,
        List<Capture> captures,
        bool followInjections,
        ref Dictionary<Region, Injection>? injected)
    {
        if (region.Length <= 0) return;

        using var tree = session.Parser.Parse(utf8.AsSpan(region.Start, region.Length));
        ScanTree(session, host, compiled, tree.RootNode, region, depth, captures, followInjections, ref injected);
    }

    private static void ScanTree(
        ParseSession session,
        CodeLanguage host,
        HighlightQuery compiled,
        Node root,
        Region region,
        int depth,
        List<Capture> captures,
        bool followInjections,
        ref Dictionary<Region, Injection>? injected)
    {
        var origin = region.Start;

        session.Cursor.ForEachMatch(compiled.Query, root, match =>
        {
            for (var i = 0; i < match.CaptureCount; i++)
            {
                var slot = compiled.SlotOf(match.CaptureIdAt(i));
                if (slot == TokenColorSlot.Default) continue;

                var node = match.NodeAt(i);
                if (node.EndByte <= node.StartByte) continue;
                captures.Add(new Capture(
                    (uint)(origin + node.StartByte),
                    (uint)(origin + node.EndByte),
                    match.PatternIndex,
                    depth,
                    slot));
            }
        });

        if (!followInjections || compiled.Injections is not { } injections) return;

        // Two patterns naming one region resolve to the later pattern, the same "specific rule
        // below the general one" convention the highlight queries follow: a <script> body goes to
        // JavaScript unless a later pattern read its lang attribute and said TypeScript. Keyed on
        // the region rather than deduplicated after the fact because matches arrive in tree order,
        // not pattern order.
        var found = injected;
        session.Cursor.ForEachMatch(injections.Query, root, match =>
        {
            var language = injections.LanguageOf(match.PatternIndex) ?? DynamicLanguageOf(injections, match);
            if (language is not { } target) return;

            for (var i = 0; i < match.CaptureCount; i++)
            {
                if (match.CaptureIdAt(i) != injections.ContentCaptureId) continue;

                var node = match.NodeAt(i);
                if (node.EndByte <= node.StartByte) continue;

                var content = new Region(origin + (int)node.StartByte, (int)(node.EndByte - node.StartByte));

                if (target == host && content == region) continue;

                found ??= [];
                if (!found.TryGetValue(content, out var existing) || existing.PatternIndex <= match.PatternIndex)
                {
                    found[content] = new Injection(target, content, match.PatternIndex);
                }
            }
        });

        injected = found;
    }

    private static CodeLanguage? DynamicLanguageOf(InjectionQuery injections, QueryMatch match)
    {
        if (injections.LanguageCaptureId is not { } languageCapture) return null;

        for (var i = 0; i < match.CaptureCount; i++)
        {
            if (match.CaptureIdAt(i) != languageCapture) continue;
            return CodeLanguages.FromInjectionName(match.NodeAt(i).Text);
        }

        return null;
    }

    /// <summary>
    /// Paints captures onto a per-character slot map, then splits each line's runs into spans.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Outer-then-inner is the first half of the precedence rule: sorting by start ascending and end
    /// descending lays the larger span down first, so a nested capture overwrites the construct
    /// containing it.
    /// </para>
    /// <para>
    /// Two captures over the <em>identical</em> range resolve to the more deeply injected one — a
    /// fenced block's own language over the <c>@text.literal</c> covering the block — and failing
    /// that to whichever pattern is written later in the query, which is the convention all but one
    /// of the vendored files are written for: a broad pattern up top that the specific ones below
    /// it override. Go is the exception and is reordered when it is vendored, because one file
    /// order has to serve one rule.
    /// </para>
    /// <para>
    /// The tie-break has to be the pattern index and not the order matches arrive in — tree-sitter
    /// does not order matches that start at the same byte by pattern, and getting this wrong is not
    /// subtle: it colors every call site and every type name as a plain variable.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<IReadOnlyList<TokenSpan>> Coalesce(
        string text,
        int byteCount,
        List<Capture> captures)
    {
        captures.Sort(static (a, b) =>
        {
            var byStart = a.StartByte.CompareTo(b.StartByte);
            if (byStart != 0) return byStart;

            var byEnd = b.EndByte.CompareTo(a.EndByte);
            if (byEnd != 0) return byEnd;

            var byDepth = a.Depth.CompareTo(b.Depth);
            if (byDepth != 0) return byDepth;

            return a.PatternIndex.CompareTo(b.PatternIndex);
        });

        var offsets = Utf8ToUtf16Offsets.For(text, byteCount);
        var slots = new byte[text.Length];

        foreach (var capture in captures)
        {
            var start = offsets.Utf16OffsetOf(capture.StartByte);
            var end = Math.Min(offsets.Utf16OffsetOf(capture.EndByte), text.Length);
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

    /// <summary>One line's spans, in the tab-expanded column space the renderer draws in.</summary>
    private static IReadOnlyList<TokenSpan> LineSpans(string text, byte[] slots, int start, int end)
    {
        List<TokenSpan>? spans = null;
        var column = 0;
        var runSlot = (byte)TokenColorSlot.Default;
        var runStart = 0;

        for (var i = start; i < end; i++)
        {
            if (slots[i] != runSlot)
            {
                AddRun(ref spans, runSlot, runStart, column);
                runSlot = slots[i];
                runStart = column;
            }

            column += text[i] == '\t' ? DiffOptions.TabWidth : 1;
        }

        AddRun(ref spans, runSlot, runStart, column);
        return (IReadOnlyList<TokenSpan>?)spans ?? [];

        static void AddRun(ref List<TokenSpan>? spans, byte slot, int from, int to)
        {
            if (slot == (byte)TokenColorSlot.Default || to <= from) return;
            (spans ??= []).Add(new TokenSpan(from, to - from, (TokenColorSlot)slot));
        }
    }

    private void LogOnce(string message)
    {
        if (Interlocked.Exchange(ref _failureLogged, 1) == 0) _log?.Invoke(message);
    }

    private readonly record struct Capture(
        uint StartByte,
        uint EndByte,
        int PatternIndex,
        int Depth,
        TokenColorSlot Slot);

    private readonly record struct Region(int Start, int Length)
    {
        public int End => Start + Length;
    }

    private readonly record struct Injection(CodeLanguage Language, Region Region, int PatternIndex);
}
