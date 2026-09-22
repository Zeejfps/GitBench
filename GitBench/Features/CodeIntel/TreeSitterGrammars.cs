using TreeSitter;

namespace GitBench.Features.CodeIntel;

/// <summary>
/// Every bundled grammar loaded once, with its parser pool and the queries both engines run over
/// it, so the outline extractor and the highlighter parse one tree per file rather than one each.
/// </summary>
/// <remarks>
/// A grammar whose native library fails to load is absent altogether; a query that fails to compile
/// leaves its slot null and the language drops out of that one engine, which is the whole of what
/// routes a language back to TextMate.
/// </remarks>
internal sealed class TreeSitterGrammars : IDisposable
{
    private const string GrammarLibrary = "tree-sitter-grammars";

    private readonly Dictionary<CodeLanguage, CompiledGrammar> _grammars = [];

    public TreeSitterGrammars(
        Action<string>? log = null,
        int? poolCapacity = null,
        Func<CodeLanguage, string>? outlineQueryText = null,
        Func<CodeLanguage, string?>? highlightQueryText = null,
        Func<CodeLanguage, string?>? injectionQueryText = null)
    {
        var capacity = poolCapacity ?? Environment.ProcessorCount;
        var readOutline = outlineQueryText ?? ReadEmbeddedOutlineQuery;
        var readHighlights = highlightQueryText ?? ReadEmbeddedHighlightQuery;
        var readInjections = injectionQueryText ?? ReadEmbeddedInjectionQuery;

        foreach (var language in CodeLanguages.Bundled)
        {
            var outlined = CodeLanguages.All.Contains(language);

            Language grammar;
            try
            {
                grammar = Language.Load(GrammarLibrary, language.GrammarName());
            }
            catch (Exception error)
            {
                if (outlined) OutlineFailure ??= error.Message;
                log?.Invoke($"Grammar unavailable for {language}: {error}");
                continue;
            }

            OutlineQuery? outline = null;
            if (outlined)
            {
                try
                {
                    outline = OutlineQuery.Compile(language, grammar, readOutline(language));
                }
                catch (Exception error)
                {
                    OutlineFailure ??= error.Message;
                    log?.Invoke($"Code intelligence unavailable for {language}: {error}");
                }
            }

            HighlightQuery? highlights = null;
            if (readHighlights(language) is { } highlightText)
            {
                try
                {
                    highlights = HighlightQuery.Compile(grammar, highlightText, readInjections(language));
                }
                catch (Exception error)
                {
                    log?.Invoke($"Tree-sitter highlighting unavailable for {language}: {error}");
                }
            }

            _grammars.Add(language, new CompiledGrammar(language, new ParseSessionPool(grammar, capacity), outline, highlights));
        }
    }

    /// <summary>Why the first outline query that could not be built could not be, or null when
    /// every one compiled.</summary>
    public string? OutlineFailure { get; }

    public bool OutlinesAny
    {
        get
        {
            foreach (var grammar in _grammars.Values)
                if (grammar.Outline is not null) return true;
            return false;
        }
    }

    public CompiledGrammar? Get(CodeLanguage language) =>
        _grammars.TryGetValue(language, out var grammar) ? grammar : null;

    /// <summary>A tree kept across one open file's edits, or null for a grammar that did not load.</summary>
    public MaintainedTree? Track(CodeLanguage language) =>
        Get(language) is { } grammar ? new MaintainedTree(grammar.Pool) : null;

    public static string ReadEmbeddedOutlineQuery(CodeLanguage language)
    {
        var resource = language.QueryResourceName();
        using var stream = typeof(TreeSitterGrammars).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded tree-sitter query '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static string? ReadEmbeddedHighlightQuery(CodeLanguage language) =>
        ReadEmbedded(language.HighlightQueryResourceName());

    private static string? ReadEmbeddedInjectionQuery(CodeLanguage language) =>
        ReadEmbedded(language.InjectionQueryResourceName());

    private static string? ReadEmbedded(string resource)
    {
        using var stream = typeof(TreeSitterGrammars).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        foreach (var grammar in _grammars.Values) grammar.Dispose();
        _grammars.Clear();
    }
}

/// <summary>One language's parser pool beside the queries compiled against its grammar.</summary>
internal sealed class CompiledGrammar : IDisposable
{
    public CompiledGrammar(CodeLanguage language, ParseSessionPool pool, OutlineQuery? outline, HighlightQuery? highlights)
    {
        Language = language;
        Pool = pool;
        Outline = outline;
        Highlights = highlights;
    }

    public CodeLanguage Language { get; }

    public ParseSessionPool Pool { get; }

    public OutlineQuery? Outline { get; }

    public HighlightQuery? Highlights { get; }

    public void Dispose()
    {
        Highlights?.Dispose();
        Outline?.Dispose();
        Pool.Dispose();
    }
}
