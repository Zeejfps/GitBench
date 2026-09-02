using System.Diagnostics;
using System.Text;

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
///
/// Returns null — "I cannot color this" — for a language it holds no query for, an over-cap file,
/// a blown budget or any failure, which is what lets <see cref="RoutedSyntaxHighlighter"/> hand
/// the file back to TextMate rather than dropping it to plain.
/// </summary>
/// <remarks>
/// Safe to call concurrently, and worth calling concurrently: a parser is per-worker by
/// construction, so unlike the TextMate engine this has no lock for a per-file lane to queue on.
/// </remarks>
internal sealed class TreeSitterSyntaxHighlighter : ISyntaxHighlighter, IDisposable
{
    // Tree-sitter's own cap, matching the outline extractor's rather than TextMate's 256 KB: this
    // engine does not backtrack, so the smaller cap would refuse files it can comfortably parse.
    public const int MaxFileBytes = TreeSitterSymbolExtractor.MaxFileBytes;

    private const string GrammarLibrary = "tree-sitter-grammars";

    // A ceiling, not a working limit — the whole corpus this was measured over runs three orders
    // of magnitude under it. It exists so a pathological file cannot hold a lane open, the same
    // job the extractor's budget does.
    private static readonly TimeSpan WholeFileBudget = TimeSpan.FromMilliseconds(750);

    private const int MaxInjectionDepth = 3;

    // TextMate's vocabulary in, ours out. Only ids we hold a highlights query for appear here —
    // "jsonc" is absent, whose comments the JSON grammar would parse as errors, and so is
    // "markdown_inline", which is a grammar rather than a language a file is written in.
    private static readonly (string LanguageId, CodeLanguage Language)[] LanguageIds =
    [
        ("csharp", CodeLanguage.CSharp),
        ("typescript", CodeLanguage.TypeScript),
        ("typescriptreact", CodeLanguage.Tsx),
        ("javascript", CodeLanguage.JavaScript),
        ("javascriptreact", CodeLanguage.JavaScript),
        ("json", CodeLanguage.Json),
        ("css", CodeLanguage.Css),
        ("yaml", CodeLanguage.Yaml),
        ("python", CodeLanguage.Python),
        ("go", CodeLanguage.Go),
        ("rust", CodeLanguage.Rust),
        ("java", CodeLanguage.Java),
        ("shellscript", CodeLanguage.Bash),
        ("c", CodeLanguage.C),
        ("toml", CodeLanguage.Toml),
        ("markdown", CodeLanguage.Markdown),
        ("html", CodeLanguage.Html),
    ];

    private readonly Dictionary<CodeLanguage, CompiledHighlights> _compiled = [];
    private readonly Action<string>? _log;
    private int _failureLogged;
    private int _budgetLogged;

    public TreeSitterSyntaxHighlighter(
        Action<string>? log = null,
        int? poolCapacity = null,
        Func<CodeLanguage, string>? queryText = null,
        Func<CodeLanguage, string?>? injectionQueryText = null)
    {
        _log = log;

        var capacity = poolCapacity ?? Environment.ProcessorCount;
        var read = queryText ?? ReadEmbeddedQuery;
        var readInjections = injectionQueryText ?? ReadEmbeddedInjectionQuery;

        foreach (var language in CodeLanguages.Bundled)
        {
            try
            {
                _compiled.Add(
                    language,
                    CompiledHighlights.Create(language, capacity, read(language), readInjections(language)));
            }
            catch (Exception error)
            {
                _log?.Invoke($"Tree-sitter highlighting unavailable for {language}: {error}");
            }
        }
    }

    /// <summary>Whether this engine, and not TextMate, should color a language.</summary>
    public bool Supports(string languageId) =>
        LanguageOf(languageId) is { } language && _compiled.ContainsKey(language);

    /// <inheritdoc cref="SyntaxHighlighter.Highlight"/>
    public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, string languageId)
    {
        ArgumentNullException.ThrowIfNull(fileText);

        if (LanguageOf(languageId) is not { } language) return null;
        if (!_compiled.TryGetValue(language, out var compiled)) return null;
        if (fileText.Length > MaxFileBytes) return null;

        var normalized = NormalizeNewlines(fileText);
        if (Encoding.UTF8.GetByteCount(normalized) > MaxFileBytes) return null;
        var utf8 = Encoding.UTF8.GetBytes(normalized);

        var watch = Stopwatch.StartNew();
        IReadOnlyList<IReadOnlyList<TokenSpan>> lines;
        try
        {
            lines = Paint(language, normalized, utf8);
        }
        catch (Exception error)
        {
            LogOnce(ref _failureLogged, $"Tree-sitter highlighting failed on a {language} file: {error}");
            return null;
        }

        if (watch.Elapsed <= WholeFileBudget) return lines;

        LogOnce(
            ref _budgetLogged,
            $"Tree-sitter highlighting exceeded its {WholeFileBudget.TotalMilliseconds:F0} ms budget " +
            $"on a {utf8.Length} byte {language} file ({watch.ElapsedMilliseconds} ms).");
        return null;
    }

    public void Dispose()
    {
        foreach (var compiled in _compiled.Values) compiled.Dispose();
        _compiled.Clear();
    }

    private static CodeLanguage? LanguageOf(string languageId)
    {
        foreach (var (id, language) in LanguageIds)
        {
            if (string.Equals(id, languageId, StringComparison.OrdinalIgnoreCase)) return language;
        }

        return null;
    }

    private static string ReadEmbeddedQuery(CodeLanguage language)
    {
        var resource = language.HighlightQueryResourceName();
        using var stream = typeof(TreeSitterSyntaxHighlighter).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded highlight query '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? ReadEmbeddedInjectionQuery(CodeLanguage language)
    {
        var resource = language.InjectionQueryResourceName();
        using var stream = typeof(TreeSitterSyntaxHighlighter).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string NormalizeNewlines(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    private IReadOnlyList<IReadOnlyList<TokenSpan>> Paint(CodeLanguage language, string text, byte[] utf8)
    {
        var captures = new List<Capture>();
        Collect(language, utf8, [new Region(0, utf8.Length)], depth: 0, captures);
        return Coalesce(text, utf8.Length, captures);
    }

    // Recursion runs after the session is returned: nesting Use on a pool of one would wait on a
    // slot this caller is holding.
    private void Collect(CodeLanguage language, byte[] utf8, List<Region> regions, int depth, List<Capture> captures)
    {
        if (!_compiled.TryGetValue(language, out var compiled)) return;

        var injected = compiled.Pool.Use(
            (compiled, utf8, regions, depth, captures, follow: depth < MaxInjectionDepth),
            static (session, s) =>
            {
                List<Injection>? found = null;
                foreach (var region in s.regions)
                {
                    Scan(session, s.compiled, s.utf8, region, s.depth, s.captures, s.follow, ref found);
                }

                return found;
            });

        if (injected is null) return;

        foreach (var group in injected.GroupBy(i => i.Language))
        {
            Collect(group.Key, utf8, [.. group.Select(i => i.Region)], depth + 1, captures);
        }
    }

    private static void Scan(
        ParseSession session,
        CompiledHighlights compiled,
        byte[] utf8,
        Region region,
        int depth,
        List<Capture> captures,
        bool followInjections,
        ref List<Injection>? injected)
    {
        if (region.Length <= 0) return;

        var origin = region.Start;
        using var tree = session.Parser.Parse(utf8.AsSpan(region.Start, region.Length));
        var root = tree.RootNode;

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

                if (target == compiled.Language && content == region) continue;

                (found ??= []).Add(new Injection(target, content));
            }
        });

        injected = found;
    }

    private static CodeLanguage? DynamicLanguageOf(CompiledInjections injections, QueryMatch match)
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

        // Byte offsets index characters directly in the overwhelmingly common all-ASCII file; only
        // a file with multi-byte characters pays for the map.
        var charOfByte = byteCount == text.Length ? null : ByteToCharMap(text, byteCount);
        var slots = new byte[text.Length];

        foreach (var capture in captures)
        {
            var start = charOfByte is null ? (int)capture.StartByte : charOfByte[capture.StartByte];
            var end = Math.Min(charOfByte is null ? (int)capture.EndByte : charOfByte[capture.EndByte], text.Length);
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

    private void LogOnce(ref int flag, string message)
    {
        if (Interlocked.Exchange(ref flag, 1) == 0) _log?.Invoke(message);
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

    private readonly record struct Injection(CodeLanguage Language, Region Region);

    /// <summary>One language's compiled query, its parser pool, and its capture ids already
    /// resolved to color slots so the per-match path is an array index.</summary>
    private sealed class CompiledHighlights : IDisposable
    {
        private readonly TokenColorSlot[] _slotOfCapture;

        private CompiledHighlights(
            CodeLanguage language,
            Query query,
            ParseSessionPool pool,
            TokenColorSlot[] slotOfCapture,
            CompiledInjections? injections)
        {
            Language = language;
            Query = query;
            Pool = pool;
            _slotOfCapture = slotOfCapture;
            Injections = injections;
        }

        public CodeLanguage Language { get; }

        public Query Query { get; }

        public ParseSessionPool Pool { get; }

        public CompiledInjections? Injections { get; }

        public TokenColorSlot SlotOf(uint captureId) => _slotOfCapture[captureId];

        public static CompiledHighlights Create(
            CodeLanguage language,
            int poolCapacity,
            string queryText,
            string? injectionQueryText)
        {
            var grammar = TreeSitter.Language.Load(GrammarLibrary, language.GrammarName());
            var query = Query.Compile(grammar, queryText);
            CompiledInjections? injections = null;

            try
            {
                var slots = new TokenColorSlot[query.CaptureCount];
                for (var id = 0u; id < query.CaptureCount; id++)
                {
                    slots[id] = HighlightCaptureMap.Map(query.CaptureName(id));
                }

                if (injectionQueryText is not null)
                {
                    injections = CompiledInjections.Create(grammar, injectionQueryText);
                }

                return new CompiledHighlights(
                    language,
                    query,
                    new ParseSessionPool(grammar, poolCapacity),
                    slots,
                    injections);
            }
            catch
            {
                injections?.Dispose();
                query.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Injections?.Dispose();
            Pool.Dispose();
            Query.Dispose();
        }
    }

    private sealed class CompiledInjections : IDisposable
    {
        private const string ContentCapture = "injection.content";
        private const string LanguageCapture = "injection.language";
        private const string LanguageProperty = "injection.language";

        private readonly CodeLanguage?[] _languageOfPattern;

        private CompiledInjections(
            Query query,
            uint contentCaptureId,
            uint? languageCaptureId,
            CodeLanguage?[] languageOfPattern)
        {
            Query = query;
            ContentCaptureId = contentCaptureId;
            LanguageCaptureId = languageCaptureId;
            _languageOfPattern = languageOfPattern;
        }

        public Query Query { get; }

        public uint ContentCaptureId { get; }

        public uint? LanguageCaptureId { get; }

        public CodeLanguage? LanguageOf(int patternIndex) => _languageOfPattern[patternIndex];

        public static CompiledInjections Create(Language grammar, string queryText)
        {
            var query = Query.Compile(grammar, queryText);

            try
            {
                if (!query.TryGetCaptureId(ContentCapture, out var contentId))
                {
                    throw new InvalidOperationException(
                        $"An injections query declares no @{ContentCapture}, so it marks no region.");
                }

                uint? languageId = query.TryGetCaptureId(LanguageCapture, out var id) ? id : null;

                var languages = new CodeLanguage?[query.PatternCount];
                for (var pattern = 0; pattern < languages.Length; pattern++)
                {
                    languages[pattern] = query.TryGetProperty(pattern, LanguageProperty, out var name)
                        ? CodeLanguages.FromInjectionName(name)
                        : null;
                }

                return new CompiledInjections(query, contentId, languageId, languages);
            }
            catch
            {
                query.Dispose();
                throw;
            }
        }

        public void Dispose() => Query.Dispose();
    }
}
