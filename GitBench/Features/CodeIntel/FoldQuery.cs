using TreeSitter;

namespace GitBench.Features.CodeIntel;

/// <summary>One language's compiled fold query: every node it captures as <c>@fold</c> is a
/// construct the reader can fold without it being a declaration.</summary>
internal sealed class FoldQuery : IDisposable
{
    private const string FoldCapture = "fold";

    private FoldQuery(Query query) => Query = query;

    public Query Query { get; }

    public static FoldQuery Compile(CodeLanguage language, Language grammar, string queryText)
    {
        var query = Query.Compile(grammar, queryText);

        try
        {
            for (var id = 0u; id < query.CaptureCount; id++)
            {
                var name = query.CaptureName(id);
                if (name != FoldCapture)
                    throw new InvalidOperationException(
                        $"The {language} fold query captures '@{name}'; the only capture it may use is '@{FoldCapture}'.");
            }

            return new FoldQuery(query);
        }
        catch
        {
            query.Dispose();
            throw;
        }
    }

    public void Dispose() => Query.Dispose();
}
