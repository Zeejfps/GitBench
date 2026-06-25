namespace GitBench.Features.Branches;

/// <summary>
/// The identity a selectable branch row is keyed on for the sliding selection bar — a local branch,
/// a remote branch, or a stash. Mirrors the discriminating fields of <see cref="BranchSelection"/>
/// (minus the tip SHA, which doesn't take part in identity): two rows that refer to the same ref
/// share a key, so the bar resolves the active row regardless of which listing snapshot produced it.
/// Stashes key on their <c>stash@{N}</c> label.
/// </summary>
public readonly record struct BranchRowKey(bool IsRemote, bool IsStash, string? RemoteName, string Name)
{
    public static BranchRowKey? From(BranchSelection? selection)
        => selection is { } s ? new BranchRowKey(s.IsRemote, s.IsStash, s.RemoteName, s.FullPath) : null;
}
