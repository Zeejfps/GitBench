namespace GitBench.Features.Diff;

/// <summary>
/// Where one foldable declaration touches the row stream. A collapsed region is not a row of its
/// own — it is an ordinary line carrying a marker — so a mark says which of the two jobs this row
/// is doing for its fold, and a row can be doing both.
/// </summary>
/// <param name="Id">The declaration's containment chain, which is what a fold is remembered by.</param>
/// <param name="Chevron">This row carries the toggle: the declaration's signature.</param>
/// <param name="Chip">This row ends a collapsed fold and shows the continuation.</param>
internal readonly record struct FoldMark(string Id, bool Collapsed, bool Chevron, bool Chip);

/// <summary>
/// Which declarations the reader has folded shut in one file. Keyed by containment chain rather
/// than by position, so a re-read of a file edited above a fold leaves it shut instead of springing
/// it open — the reconcile tick runs twice a minute, and folds that survived only an untouched file
/// would be folds that never survived anything.
/// </summary>
/// <remarks>
/// Carries the path it belongs to so a state built for one file can never be applied to the next
/// one to arrive. Lives on the view model, touched only on the UI thread, not persisted.
/// </remarks>
internal sealed record FoldState(string Path, IReadOnlySet<string> Collapsed)
{
    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>(StringComparer.Ordinal);

    public static FoldState Open(string path) => new(path, Nothing);

    public bool IsCollapsed(string id) => Collapsed.Contains(id);

    public FoldState Toggled(string id)
    {
        var next = new HashSet<string>(Collapsed, StringComparer.Ordinal);
        if (!next.Remove(id)) next.Add(id);
        return this with { Collapsed = next };
    }
}
