using GitBench.Features.CodeIntel;
using GitBench.Infrastructure;
using GitBench.Theming;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace GitBench.Features.Diff;

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
internal sealed class SyntaxHighlighter : ISyntaxHighlighter
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

    private static readonly TimeSpan WarmUpTimeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, IGrammar?> _grammarCache = new();
    // TextMateSharp grammars are not safe for concurrent tokenization; serialize all engine
    // access. The coordinator only highlights one file's two sides sequentially, so contention
    // is nil in practice — the lock is correctness insurance, not a hot path.
    private readonly object _lock = new();
    private RegistryOptions? _options;
    private Registry? _registry;

    /// <summary>Builds the TextMate registry ahead of the first file, off whatever thread this is
    /// called on — the one cost here that is worth paying before it is asked for.</summary>
    public void Warm()
    {
        lock (_lock) EnsureRegistry();
    }

    public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, FileLanguage language) =>
        language switch
        {
            FileLanguage.TreeSitter(var parsed) => Highlight(fileText, parsed.TextMateId()),
            FileLanguage.TextMate(var id) => Highlight(fileText, id),
            FileLanguage.None => null,
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
        };

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
        var lines = TextLines.SplitKeepingLast(fileText);
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

    // The theme only drives TextMateSharp's own color resolution, which we don't use — we map
    // scopes to the GitBench palette ourselves. Any valid theme works here. The wrapper adds
    // grammars TextMateSharp doesn't bundle (Svelte); the inner options still serve every
    // standard scope, including the languages a Svelte block embeds.
    private (RegistryOptions Options, Registry Registry) EnsureRegistry()
    {
        if (_options is { } options && _registry is { } registry) return (options, registry);
        _options = new RegistryOptions(ThemeName.DarkPlus);
        _registry = new Registry(new BundledGrammarRegistryOptions(_options));
        return (_options, _registry);
    }

    private IGrammar? GetGrammar(string languageId)
    {
        lock (_lock)
        {
            if (_grammarCache.TryGetValue(languageId, out var cached)) return cached;
            var (options, registry) = EnsureRegistry();
            IGrammar? grammar = null;
            try
            {
                // Custom bundled grammars (Svelte) aren't in RegistryOptions' language table, so
                // resolve their scope from the wrapper first, then fall back to the bundled set.
                var scope = BundledGrammarRegistryOptions.ScopeForLanguageId(languageId)
                            ?? options.GetScopeByLanguageId(languageId);
                if (!string.IsNullOrEmpty(scope))
                    grammar = registry.LoadGrammar(scope);
            }
            catch
            {
                grammar = null;
            }

            // TextMateSharp compiles rules on the first TokenizeLine (markdown: 178 ms, then
            // 0.1 ms). Pay it here so it lands outside a file's WholeFileBudget rather than on
            // whichever file of that language happens to be first.
            if (grammar != null)
            {
                try { grammar.TokenizeLine(string.Empty, null, WarmUpTimeout); }
                catch { /* warm-up is an optimization; a failed one costs only the saving */ }
            }
            _grammarCache[languageId] = grammar; // cache nulls too: an unknown id won't resolve on retry
            return grammar;
        }
    }

}
