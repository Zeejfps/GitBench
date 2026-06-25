namespace GitBench.Features.Branches;

/// <summary>
/// One rendered line in the branches sidebar, as a closed set of variants flattened from a
/// <see cref="BranchListing"/> plus collapse state. Each variant carries exactly the data its kind
/// needs — all non-null — so the row widget and the click/menu dispatch pattern-match a row to its
/// data instead of reading a bag of nullable fields. <see cref="Depth"/> drives the row's tree
/// indent; the display label lives on the variant that owns it (section headers are localized by the
/// widget and carry none).
///
/// Implements <see cref="IDisposable"/> only so the rows can flow through the value-equality keyed
/// list that projects them into the view (a row IS its own key); there is nothing to release.
/// </summary>
public abstract record BranchRow(int Depth) : IDisposable
{
    // The selection identity for selectable rows (branches, stashes); null for headers and folders.
    public virtual BranchRowKey? SelectionKey => null;

    public void Dispose() { }
}

/// A row that shows a chevron and toggles its subtree open/closed.
public interface ICollapsibleRow
{
    bool IsOpen { get; }
}

public sealed record LocalHeaderRow(int Depth, bool IsOpen) : BranchRow(Depth), ICollapsibleRow;

public sealed record RemotesHeaderRow(int Depth, bool IsOpen) : BranchRow(Depth), ICollapsibleRow;

public sealed record StashesHeaderRow(int Depth, bool IsOpen) : BranchRow(Depth), ICollapsibleRow;

public sealed record RemoteHeaderRow(int Depth, string RemoteName, bool IsOpen)
    : BranchRow(Depth), ICollapsibleRow;

public sealed record FolderRow(int Depth, BranchFolder Folder, string DisplayName, bool IsOpen)
    : BranchRow(Depth), ICollapsibleRow;

public sealed record LocalBranchRow(
    int Depth,
    string Name,
    string DisplayName,
    string TipSha,
    bool IsHead,
    int? AheadBy,
    int? BehindBy,
    BranchUpstreamState UpstreamState) : BranchRow(Depth)
{
    public override BranchRowKey? SelectionKey => new(IsRemote: false, IsStash: false, RemoteName: null, Name);
}

public sealed record RemoteBranchRow(int Depth, string RemoteName, string Name, string DisplayName, string TipSha)
    : BranchRow(Depth)
{
    public override BranchRowKey? SelectionKey => new(IsRemote: true, IsStash: false, RemoteName, Name);
}

// Label is the "stash@{N}" ref; DisplayName is the stash subject (also its menu label).
public sealed record StashRow(int Depth, int Index, string Label, string DisplayName, string TipSha)
    : BranchRow(Depth)
{
    public override BranchRowKey? SelectionKey => new(IsRemote: false, IsStash: true, RemoteName: null, Label);
}

// IsStash flags the selection as pointing at a stash entry; FullPath is then the
// stash ref label ("stash@{N}") rather than a branch name.
public readonly record struct BranchSelection(bool IsRemote, bool IsStash, string? RemoteName, string FullPath, string TipSha);
