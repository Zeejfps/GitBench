using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace GitGui;

/// <summary>
/// The only module that touches TextMateSharp. Tokenizes a whole file top-to-bottom, threading
/// the per-line <see cref="IStateStack"/> so a multi-line comment or string stays correctly
/// colored on every line it spans, and returns per-line <see cref="TokenSpan"/> lists in
/// tab-expanded column space (so spans align 1:1 with the diff's tab-expanded rendered text).
///
/// Returns null — meaning "render plain" — for an unknown grammar, an over-cap file, or any
/// tokenize failure / per-line timeout. It never throws to callers, so highlighting failures
/// degrade silently to today's plain rendering.
/// </summary>
internal sealed class SyntaxHighlighter
{
    // Files larger than this skip highlighting entirely (GitHub Desktop uses a comparable
    // ~256 KB heuristic). Bounds worst-case tokenize cost on a huge blob.
    public const int MaxFileChars = 256 * 1024;

    // Caps Oniguruma backtracking on any one line — the engine returns partial tokens rather
    // than spinning, so a single pathological line can't stall the diff.
    private static readonly TimeSpan PerLineTimeout = TimeSpan.FromMilliseconds(100);

    // Aggregate guard: if tokenizing a whole file exceeds this, bail to plain. The per-line
    // cap bounds one line; this bounds the sum so a file of many slow-but-under-cap lines
    // can't bog the worker. Generous enough that normal files never trip it.
    private static readonly TimeSpan WholeFileBudget = TimeSpan.FromMilliseconds(750);

    private readonly RegistryOptions _options;
    private readonly Registry _registry;
    private readonly Dictionary<string, IGrammar?> _grammarCache = new();
    // TextMateSharp grammars are not safe for concurrent tokenization; serialize all engine
    // access. The coordinator only highlights one file's two sides sequentially, so contention
    // is nil in practice — the lock is correctness insurance, not a hot path.
    private readonly object _lock = new();

    public SyntaxHighlighter()
    {
        // The theme only drives TextMateSharp's own color resolution, which we don't use — we
        // map scopes to the GitBench palette ourselves. Any valid theme works here.
        _options = new RegistryOptions(ThemeName.DarkPlus);
        _registry = new Registry(_options);
    }

    /// <summary>
    /// Tokenizes <paramref name="fileText"/> as <paramref name="languageId"/> and returns one
    /// span list per source line (index 0 = first line), or null to signal plain rendering.
    /// Lines with no non-default tokens yield an empty list rather than null.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, string languageId)
    {
        if (fileText.Length > MaxFileChars) return null;

        var grammar = GetGrammar(languageId);
        if (grammar is null) return null;

        try
        {
            lock (_lock)
            {
                return Tokenize(grammar, fileText);
            }
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<IReadOnlyList<TokenSpan>>? Tokenize(IGrammar grammar, string fileText)
    {
        var lines = SplitLines(fileText);
        var result = new List<IReadOnlyList<TokenSpan>>(lines.Count);
        IStateStack? state = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var raw in lines)
        {
            if (sw.Elapsed > WholeFileBudget)
                return null; // aggregate tokenize budget blown → fall back to plain
            var expanded = DiffText.ExpandTabs(raw);
            var tokenized = grammar.TokenizeLine(expanded, state, PerLineTimeout);
            state = tokenized.RuleStack;
            result.Add(BuildSpans(tokenized.Tokens, expanded.Length));
        }
        return result;
    }

    private static IReadOnlyList<TokenSpan> BuildSpans(IReadOnlyList<IToken> tokens, int lineLength)
    {
        if (tokens.Count == 0) return Array.Empty<TokenSpan>();
        List<TokenSpan>? spans = null;
        foreach (var t in tokens)
        {
            var start = t.StartIndex;
            var end = Math.Min(t.EndIndex, lineLength);
            if (end <= start) continue;
            var slot = SlotFor(t.Scopes);
            if (slot == TokenColorSlot.Default) continue; // plain runs need no span
            (spans ??= new List<TokenSpan>()).Add(new TokenSpan(start, end - start, slot));
        }
        return (IReadOnlyList<TokenSpan>?)spans ?? Array.Empty<TokenSpan>();
    }

    // TextMate orders a token's scopes least → most specific, so walk from the back and take
    // the first scope that resolves to a real slot. This keeps comment/string delimiters
    // (a more specific punctuation.definition.* scope) colored as their parent.
    private static TokenColorSlot SlotFor(IReadOnlyList<string> scopes)
    {
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var slot = ScopeColorMap.Map(scopes[i]);
            if (slot != TokenColorSlot.Default) return slot;
        }
        return TokenColorSlot.Default;
    }

    private IGrammar? GetGrammar(string languageId)
    {
        lock (_lock)
        {
            if (_grammarCache.TryGetValue(languageId, out var cached)) return cached;
            IGrammar? grammar = null;
            try
            {
                var scope = _options.GetScopeByLanguageId(languageId);
                if (!string.IsNullOrEmpty(scope))
                    grammar = _registry.LoadGrammar(scope);
            }
            catch
            {
                grammar = null;
            }
            _grammarCache[languageId] = grammar; // cache nulls too: an unknown id won't resolve on retry
            return grammar;
        }
    }

    // Splits into lines on \n, tolerating \r\n, and always keeps a final element so 1-based
    // source line numbers index straight into the result (a file ending in a newline yields a
    // trailing empty line, matching how the diff numbers its lines).
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i;
            if (end > start && text[end - 1] == '\r') end--;
            lines.Add(text.Substring(start, end - start));
            start = i + 1;
        }
        lines.Add(text.Substring(start));
        return lines;
    }
}
