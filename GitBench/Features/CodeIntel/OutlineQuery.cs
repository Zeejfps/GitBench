using TreeSitter;

namespace GitBench.Features.CodeIntel;

/// <summary>One language's compiled outline query and its capture protocol resolved to ids.</summary>
internal sealed class OutlineQuery : IDisposable
{
    private const string DefinitionCapturePrefix = "def.";
    private const string NameCapture = "name";
    private const string BodyCapture = "body";
    private const string ExtentCapture = "extent";

    private readonly Dictionary<uint, SymbolKind> _definitionCaptures;

    private OutlineQuery(
        Query query,
        Dictionary<uint, SymbolKind> definitionCaptures,
        uint nameCaptureId,
        uint bodyCaptureId,
        bool hasBodyCapture,
        uint extentCaptureId,
        bool hasExtentCapture,
        IReadOnlyList<string> leadingDecorations)
    {
        Query = query;
        _definitionCaptures = definitionCaptures;
        NameCaptureId = nameCaptureId;
        BodyCaptureId = bodyCaptureId;
        HasBodyCapture = hasBodyCapture;
        ExtentCaptureId = extentCaptureId;
        HasExtentCapture = hasExtentCapture;
        LeadingDecorations = leadingDecorations;
    }

    public Query Query { get; }

    public uint NameCaptureId { get; }

    public uint BodyCaptureId { get; }

    public bool HasBodyCapture { get; }

    public uint ExtentCaptureId { get; }

    public bool HasExtentCapture { get; }

    public IReadOnlyList<string> LeadingDecorations { get; }

    public static OutlineQuery Compile(CodeLanguage language, Language grammar, string queryText)
    {
        var query = Query.Compile(grammar, queryText);

        try
        {
            var definitionCaptures = new Dictionary<uint, SymbolKind>();
            uint nameCaptureId = 0;
            var hasNameCapture = false;
            uint bodyCaptureId = 0;
            var hasBodyCapture = false;
            uint extentCaptureId = 0;
            var hasExtentCapture = false;

            for (var id = 0u; id < query.CaptureCount; id++)
            {
                var name = query.CaptureName(id);

                if (name.StartsWith(DefinitionCapturePrefix, StringComparison.Ordinal))
                {
                    var suffix = name.AsSpan(DefinitionCapturePrefix.Length);
                    if (!SymbolKinds.TryParseCaptureSuffix(suffix, out var kind))
                    {
                        throw new InvalidOperationException(
                            $"The {language} query captures '@{name}', but '{suffix}' is not a symbol kind. " +
                            $"Legal kinds: {string.Join(", ", SymbolKinds.CaptureSuffixes)}.");
                    }

                    definitionCaptures.Add(id, kind);
                }
                else if (name == NameCapture)
                {
                    nameCaptureId = id;
                    hasNameCapture = true;
                }
                else if (name == BodyCapture)
                {
                    bodyCaptureId = id;
                    hasBodyCapture = true;
                }
                else if (name == ExtentCapture)
                {
                    extentCaptureId = id;
                    hasExtentCapture = true;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"The {language} query captures '@{name}', which is not part of the capture protocol. " +
                        $"Use '@{DefinitionCapturePrefix}<kind>', '@{NameCapture}', '@{BodyCapture}' " +
                        $"or '@{ExtentCapture}'.");
                }
            }

            if (definitionCaptures.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The {language} query declares no '@{DefinitionCapturePrefix}<kind>' capture, " +
                    "so it can never produce an outline node.");
            }

            if (!hasNameCapture)
            {
                throw new InvalidOperationException(
                    $"The {language} query declares no '@{NameCapture}' capture, " +
                    "so every match would be discarded.");
            }

            return new OutlineQuery(
                query,
                definitionCaptures,
                nameCaptureId,
                bodyCaptureId,
                hasBodyCapture,
                extentCaptureId,
                hasExtentCapture,
                language.LeadingDecorationNodeTypes());
        }
        catch
        {
            query.Dispose();
            throw;
        }
    }

    public bool TryReadDefinition(QueryMatch match, out Node definition, out SymbolKind kind)
    {
        for (var i = 0; i < match.CaptureCount; i++)
        {
            if (_definitionCaptures.TryGetValue(match.CaptureIdAt(i), out kind))
            {
                definition = match.NodeAt(i);
                return true;
            }
        }

        definition = default;
        kind = default;
        return false;
    }

    public void Dispose() => Query.Dispose();
}
