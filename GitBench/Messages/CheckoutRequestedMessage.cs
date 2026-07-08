namespace GitBench.Messages;

// Raised by history-view badge clicks. Handled by BranchesViewModel, which owns the checkout
// flow (op lanes, worktree-sibling redirect, optimistic head state) — the commits side can't
// call it directly because Context.Require would build a fresh transient with empty state.
// RemoteName != null marks a remote-branch badge: checkout goes through the tracking-branch
// dialog unless a same-named local branch already exists.
public readonly record struct CheckoutRequestedMessage(Guid RepoId, string BranchName, string? RemoteName = null);
