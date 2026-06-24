namespace GitBench.Features.Branches;

/// <summary>
/// One rendered line in the branches sidebar, as a closed set of variants. Each variant
/// carries exactly the data its kind needs — all non-null — so the renderer and the
/// click/context dispatch pattern-match a row to its data instead of reading a bag of
/// nullable fields guarded by a kind tag. <see cref="Indent"/> (pixels from the row's
/// left edge) and <see cref="DisplayName"/> (the rendered label) are common to every row;
/// everything else lives on the variant that owns it.
/// </summary>
public abstract record BranchRow(float Indent, string DisplayName);

/// A row that shows a chevron and toggles its subtree open/closed.
public interface ICollapsibleRow
{
    bool IsOpen { get; }
}

public sealed record LocalHeaderRow(bool IsOpen, float Indent, string DisplayName)
    : BranchRow(Indent, DisplayName), ICollapsibleRow;

public sealed record RemotesHeaderRow(bool IsOpen, float Indent, string DisplayName)
    : BranchRow(Indent, DisplayName), ICollapsibleRow;

public sealed record StashesHeaderRow(bool IsOpen, float Indent, string DisplayName)
    : BranchRow(Indent, DisplayName), ICollapsibleRow;

public sealed record RemoteHeaderRow(string RemoteName, bool IsOpen, float Indent, string DisplayName)
    : BranchRow(Indent, DisplayName), ICollapsibleRow;

public sealed record FolderRow(BranchFolder Folder, bool IsOpen, float Indent, string DisplayName)
    : BranchRow(Indent, DisplayName), ICollapsibleRow;

public sealed record LocalBranchRow(
    string Name,
    string TipSha,
    bool IsHead,
    int? AheadBy,
    int? BehindBy,
    BranchUpstreamState UpstreamState,
    float Indent,
    string DisplayName) : BranchRow(Indent, DisplayName);

public sealed record RemoteBranchRow(string RemoteName, string Name, string TipSha, float Indent, string DisplayName)
    : BranchRow(Indent, DisplayName);

// Label is the "stash@{N}" ref; DisplayName is the stash subject (also its menu label).
public sealed record StashRow(int Index, string Label, string TipSha, float Indent, string DisplayName)
    : BranchRow(Indent, DisplayName);

// IsStash flags the selection as pointing at a stash entry; FullPath is then the
// stash ref label ("stash@{N}") rather than a branch name.
public readonly record struct BranchSelection(bool IsRemote, bool IsStash, string? RemoteName, string FullPath, string TipSha)
{
    public bool Matches(BranchRow row) => row switch
    {
        LocalBranchRow b => !IsRemote && !IsStash && b.Name == FullPath,
        RemoteBranchRow b => IsRemote && !IsStash && b.RemoteName == RemoteName && b.Name == FullPath,
        StashRow s => IsStash && s.Label == FullPath,
        _ => false,
    };
}
