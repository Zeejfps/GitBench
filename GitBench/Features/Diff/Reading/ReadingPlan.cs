namespace GitBench.Features.Diff.Reading;

/// <summary>An inclusive range of row ordinals to hide outright.</summary>
internal sealed record ReadingRemoval(int StartRow, int EndRow);

/// <summary>
/// An inclusive range of two or more rows replaced by one generated ellipsis row.
/// </summary>
/// <remarks>The fold's text is derived from the source (its marker and common indent); the plan
/// supplies coordinates only, so a fold can never introduce prose.</remarks>
internal sealed record ReadingFold(int StartRow, int EndRow);

/// <summary>
/// A within-line elision: <see cref="New"/> must be <see cref="Old"/> with spans deleted and
/// each deletion marked by an ellipsis.
/// </summary>
internal sealed record ReadingElision(int Row, string Old, string New);

/// <summary>
/// What to hide, fold and elide to turn a diff into a reading diff, expressed entirely in
/// <see cref="ReadingRowIndex"/> coordinates.
/// </summary>
/// <remarks>
/// A plan is always complete rather than incremental: it is stated against the original
/// numbering, so revising it never means tracking what an earlier draft already did.
/// </remarks>
internal sealed record ReadingPlan(
    IReadOnlyList<ReadingRemoval> Remove,
    IReadOnlyList<ReadingElision> Replace,
    IReadOnlyList<ReadingFold> Fold,
    string? Summary = null)
{
    public static readonly ReadingPlan Empty = new([], [], []);
}
