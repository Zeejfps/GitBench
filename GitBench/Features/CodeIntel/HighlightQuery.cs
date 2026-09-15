using GitBench.Theming;

using TreeSitter;

namespace GitBench.Features.CodeIntel;

/// <summary>One language's compiled highlights query with its capture ids already resolved to color
/// slots so the per-match path is an array index, plus the injections query where it embeds
/// other languages.</summary>
internal sealed class HighlightQuery : IDisposable
{
    private readonly TokenColorSlot[] _slotOfCapture;

    private HighlightQuery(Query query, TokenColorSlot[] slotOfCapture, InjectionQuery? injections)
    {
        Query = query;
        _slotOfCapture = slotOfCapture;
        Injections = injections;
    }

    public Query Query { get; }

    public InjectionQuery? Injections { get; }

    public TokenColorSlot SlotOf(uint captureId) => _slotOfCapture[captureId];

    public static HighlightQuery Compile(Language grammar, string queryText, string? injectionQueryText)
    {
        var query = Query.Compile(grammar, queryText);
        InjectionQuery? injections = null;

        try
        {
            var slots = new TokenColorSlot[query.CaptureCount];
            for (var id = 0u; id < query.CaptureCount; id++)
            {
                slots[id] = HighlightCaptureMap.Map(query.CaptureName(id));
            }

            if (injectionQueryText is not null)
            {
                injections = InjectionQuery.Compile(grammar, injectionQueryText);
            }

            return new HighlightQuery(query, slots, injections);
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
        Query.Dispose();
    }
}

internal sealed class InjectionQuery : IDisposable
{
    private const string ContentCapture = "injection.content";
    private const string LanguageCapture = "injection.language";
    private const string LanguageProperty = "injection.language";

    private readonly CodeLanguage?[] _languageOfPattern;

    private InjectionQuery(
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

    public static InjectionQuery Compile(Language grammar, string queryText)
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

            return new InjectionQuery(query, contentId, languageId, languages);
        }
        catch
        {
            query.Dispose();
            throw;
        }
    }

    public void Dispose() => Query.Dispose();
}
