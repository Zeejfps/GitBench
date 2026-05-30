namespace GitGui;

public enum BranchRowKind { LocalHeader, RemotesHeader, RemoteHeader, Folder, LocalBranch, RemoteBranch, StashesHeader, Stash }

public sealed class BranchRow
{
    public BranchRow(BranchRowKind kind, string displayName, float indent, bool isOpen)
    {
        Kind = kind;
        DisplayName = displayName;
        Indent = indent;
        IsOpen = isOpen;
    }
    public BranchRowKind Kind { get; }
    public string DisplayName { get; }
    public float Indent { get; }
    public bool IsOpen { get; }
    public string? TipSha { get; init; }
    public bool IsHead { get; init; }
    public string? RemoteName { get; init; }
    public string? FullPath { get; init; }
    public string? FolderKey { get; init; }
    public int? AheadBy { get; init; }
    public int? BehindBy { get; init; }
    public int? StashIndex { get; init; }
    public BranchUpstreamState UpstreamState { get; init; } = BranchUpstreamState.Tracked;
}

// IsStash flags the selection as pointing at a stash entry; FullPath is then the
// stash ref label ("stash@{N}") rather than a branch name.
public readonly record struct BranchSelection(bool IsRemote, bool IsStash, string? RemoteName, string FullPath, string TipSha)
{
    public bool Matches(BranchRow row) => row.Kind switch
    {
        BranchRowKind.LocalBranch => !IsRemote && !IsStash && row.FullPath == FullPath,
        BranchRowKind.RemoteBranch => IsRemote && !IsStash && row.RemoteName == RemoteName && row.FullPath == FullPath,
        BranchRowKind.Stash => IsStash && row.FullPath == FullPath,
        _ => false,
    };
}
