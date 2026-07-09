namespace GitBench.Git;

public enum DetachedHeadKind
{
    // Not detached, or an operation is in progress, or the commit is reachable and there's
    // nothing useful to say.
    None = 0,
    // Detached, but HEAD sits on (or can fast-forward onto) a branch tip — offer to switch
    // onto it. Used for submodules, which the superproject routinely parks on a detached HEAD.
    OnBranchTip = 1,
    // Detached with commits reachable only from HEAD — they'd be orphaned by a checkout.
    AtRisk = 2,
}

// Branch carries the short name to switch onto (OnBranchTip) — null otherwise.
public sealed record DetachedHeadReport(DetachedHeadKind Kind, string? Branch = null)
{
    public static DetachedHeadReport None { get; } = new(DetachedHeadKind.None);
}
