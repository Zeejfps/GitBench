using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using ZGF.Geometry;

namespace GitBench.Features.LanguageServers;

internal interface IFilePositionSurface
{
    TextPosition? HitTestFilePosition(PointF point);
}

internal interface IHoverSurface : IFilePositionSurface
{
    /// <summary>What the server said about a line, for the card that shows it. Read from the
    /// surface rather than asked of the servers again, so the message on the card is the one whose
    /// squiggle the reader is pointing at.</summary>
    IReadOnlyList<Diagnostic> DiagnosticsOn(FileLine line);
}

internal interface IHoverSource
{
    bool Handles(string absolutePath);

    Task<HoverText?> HoverAsync(
        string repoRoot, string absolutePath, FileLine line, RawColumn column, CancellationToken ct);
}

internal interface IHoverPresenter
{
    void Show(object owner, HoverText hover, RectF anchorCanvas);

    void Hide(object owner);
}

/// <summary>
/// The surface a definition link lives on: it turns a pixel into the identifier under it, and shows
/// or clears the link decoration on one. Separate from the hit-test alone because the decoration is
/// state the surface holds between events — the pointer stops moving while a modifier goes down.
/// </summary>
internal interface IDefinitionSurface : IFilePositionSurface
{
    FileSpan? HitTestIdentifier(PointF point);

    void ShowDefinitionLink(FileSpan? link);
}

internal interface IDefinitionSource
{
    bool CanDefine(string absolutePath);

    Task<DefinitionReply> DefineAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken ct);
}

internal interface IReferenceSource
{
    /// <summary>
    /// Whether a usage count for this file is worth asking for. Optimistic while the server for it
    /// has yet to launch: the answer is wanted synchronously, before the row that would carry the
    /// count is built, and a "no" that turns into a "yes" a second later inserts rows into text
    /// somebody is already reading.
    /// </summary>
    bool CanReference(string absolutePath);

    Task<ReferenceReply> ReferencesAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken ct);
}

internal interface ICompletionSource
{
    /// <summary>Whether a server may answer for this file. Optimistic while it has yet to launch:
    /// the question costs one request, and the list falls back on the file's own words.</summary>
    bool CanComplete(string absolutePath);

    /// <summary>The characters that should open a list by themselves, or none where no running
    /// server has said.</summary>
    IReadOnlyList<char> CompletionTriggers(string absolutePath);

    Task<CompletionReply> CompletionsAsync(
        string absolutePath, FileLine line, RawColumn column, CompletionAsk ask, CancellationToken ct);
}

internal interface ISemanticTokenSource
{
    /// <summary>Whether asking is worth it. Optimistic while the server has yet to launch, like
    /// <see cref="IReferenceSource.CanReference"/>: a wrong yes costs one request that answers
    /// nothing, and the colors it would bring are an addition, never a row to reserve.</summary>
    bool CanClassify(string absolutePath);

    Task<SemanticTokensReply> SemanticTokensAsync(string absolutePath, CancellationToken ct);
}
