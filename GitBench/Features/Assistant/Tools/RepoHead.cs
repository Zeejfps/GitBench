using GitBench.Git;

namespace GitBench.Features.Assistant.Tools;

/// <summary>The branch a checkout has out right now, asked of git rather than of the Repo record.</summary>
/// <remarks>
/// <see cref="Repo.Branch"/> cannot answer this. A <see cref="Repo"/> is an immutable record and the
/// registry publishes a branch switch by replacing it, so anything holding one across the switch holds
/// the branch that was out when it was handed the record — and the assistant holds one for the life of
/// a session. The field is a throttled worktree-sweep result besides, which is enough to mark a branch
/// "taken" in a list and not enough to decide what is under review.
/// </remarks>
internal static class RepoHead
{
    public static string? Branch(IGitService git, Repo repo) =>
        git.GetStatusSummary(repo) is { IsDetached: false, Branch: { Length: > 0 } branch } ? branch : null;
}
