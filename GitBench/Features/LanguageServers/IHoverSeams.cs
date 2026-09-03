using GitBench.Features.Diff;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using ZGF.Geometry;

namespace GitBench.Features.LanguageServers;

internal interface IFilePositionSurface
{
    FilePositionHit? HitTestFilePosition(PointF point);
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

/// <summary>
/// What a server said about a symbol: where it is declared, and the span of the symbol itself back
/// in the asking file when the server bothered to say. The span is what the link is drawn over —
/// without it the caller falls back to the word it found on screen, which is right for every server
/// and exact for the ones that answer about a qualified name rather than the word under the cursor.
/// </summary>
internal sealed record DefinitionReply(IReadOnlyList<DefinitionTarget> Targets, OptionalRange Origin)
{
    public static readonly DefinitionReply Nothing = new([], OptionalRange.Absent);
}

internal interface IDefinitionSource
{
    bool CanDefine(string absolutePath);

    Task<DefinitionReply> DefineAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken ct);
}

/// <summary>
/// Where a symbol is used, the declaration itself excluded — so the number of sites is the number
/// a reader is shown.
/// </summary>
/// <remarks>
/// A symbol nothing uses and a question that could not be put are held apart, rather than both
/// arriving as an empty list, because the count is shown as a sentence about the code: "no usages"
/// over a symbol whose server never started says the code is dead, which is the one thing this
/// feature must never say by accident. <see cref="Answered"/> with an empty list is the real zero.
/// </remarks>
internal abstract record ReferenceReply
{
    private ReferenceReply() { }

    /// <summary>Nobody could be asked: no server for the file, a server that does not answer the
    /// question, one that never finished starting, or a file it could not be shown.</summary>
    public sealed record Unavailable : ReferenceReply
    {
        public static readonly Unavailable Instance = new();
    }

    public sealed record Answered(IReadOnlyList<DefinitionTarget> Sites) : ReferenceReply;
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
