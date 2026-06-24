namespace GitBench.Features.Branches;

/// <summary>
/// Whether a branch (or branch folder) lives in the local namespace or under a named
/// remote. Collapses the former (isRemote, remoteName) pair into one value: a local scope
/// never carries a remote name, and a remote scope always does — neither nonsensical
/// combination is representable.
/// </summary>
public readonly record struct BranchScope
{
    private BranchScope(string? remoteName) => RemoteName = remoteName;

    /// The remote's name (e.g. "origin"); null for the local scope.
    public string? RemoteName { get; }

    public bool IsRemote => RemoteName != null;

    public static readonly BranchScope Local = new((string?)null);

    public static BranchScope Remote(string remoteName) => new(remoteName);
}
