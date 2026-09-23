using TreeSitter;

namespace GitBench.Features.CodeIntel;

/// <summary>One language's compiled fold query: every node it captures as <c>@fold</c> is a
/// construct the reader can fold without it being a declaration.</summary>
/// <remarks>
/// A match may also capture <c>@end</c>, the node the fold stops at when that is short of the
/// folded node's own end — a Python <c>if</c> whose node runs on through its <c>elif</c> and
/// <c>else</c> branches, each of which folds on its own.
/// </remarks>
internal sealed class FoldQuery : IDisposable
{
    private const string FoldCapture = "fold";
    private const string EndCapture = "end";

    private FoldQuery(Query query, uint foldCaptureId, uint? endCaptureId)
    {
        Query = query;
        FoldCaptureId = foldCaptureId;
        EndCaptureId = endCaptureId;
    }

    public Query Query { get; }

    public uint FoldCaptureId { get; }

    public uint? EndCaptureId { get; }

    public static FoldQuery Compile(CodeLanguage language, Language grammar, string queryText)
    {
        var query = Query.Compile(grammar, queryText);

        try
        {
            uint? fold = null;
            uint? end = null;
            for (var id = 0u; id < query.CaptureCount; id++)
            {
                switch (query.CaptureName(id))
                {
                    case FoldCapture:
                        fold = id;
                        break;
                    case EndCapture:
                        end = id;
                        break;
                    case var name:
                        throw new InvalidOperationException(
                            $"The {language} fold query captures '@{name}'; it may use only '@{FoldCapture}' and '@{EndCapture}'.");
                }
            }

            if (fold is not { } foldId)
                throw new InvalidOperationException($"The {language} fold query declares no '@{FoldCapture}' capture.");

            return new FoldQuery(query, foldId, end);
        }
        catch
        {
            query.Dispose();
            throw;
        }
    }

    public void Dispose() => Query.Dispose();
}
