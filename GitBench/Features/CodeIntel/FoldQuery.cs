using TreeSitter;

namespace GitBench.Features.CodeIntel;

/// <summary>One language's compiled fold query: every node it captures as <c>@fold</c> is a
/// construct the reader can fold without it being a declaration.</summary>
/// <remarks>
/// Where the folded node runs on through branches that fold on their own — an <c>if</c>'s
/// <c>elif</c> and <c>else</c> — a match says where its fold stops instead. <c>@end</c> names the
/// last node the fold covers, a Python <c>if</c>'s own block. <c>@stop</c> names a branch the fold
/// ends before; one pattern matches once per branch, the earliest wins, and a node that matches
/// no stopped pattern — an <c>if</c> without an <c>else</c> — folds whole through its plain
/// <c>@fold</c> pattern.
/// </remarks>
internal sealed class FoldQuery : IDisposable
{
    private const string FoldCapture = "fold";
    private const string EndCapture = "end";
    private const string StopCapture = "stop";

    private FoldQuery(Query query, uint foldCaptureId, uint? endCaptureId, uint? stopCaptureId)
    {
        Query = query;
        FoldCaptureId = foldCaptureId;
        EndCaptureId = endCaptureId;
        StopCaptureId = stopCaptureId;
    }

    public Query Query { get; }

    public uint FoldCaptureId { get; }

    public uint? EndCaptureId { get; }

    public uint? StopCaptureId { get; }

    public static FoldQuery Compile(CodeLanguage language, Language grammar, string queryText)
    {
        var query = Query.Compile(grammar, queryText);

        try
        {
            uint? fold = null;
            uint? end = null;
            uint? stop = null;
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
                    case StopCapture:
                        stop = id;
                        break;
                    case var name:
                        throw new InvalidOperationException(
                            $"The {language} fold query captures '@{name}'; it may use only " +
                            $"'@{FoldCapture}', '@{EndCapture}' and '@{StopCapture}'.");
                }
            }

            if (fold is not { } foldId)
                throw new InvalidOperationException($"The {language} fold query declares no '@{FoldCapture}' capture.");

            return new FoldQuery(query, foldId, end, stop);
        }
        catch
        {
            query.Dispose();
            throw;
        }
    }

    public void Dispose() => Query.Dispose();
}
