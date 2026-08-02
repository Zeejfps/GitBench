using GitBench.Features.Commits;
using GitBench.Features.Review;

namespace GitBench.Git;

public interface IGitHistoryReader
{
    Fetched<CommitSnapshot> Load(Repo repo, int cap);
    Fetched<CommitDetails> LoadDetails(Repo repo, string sha);
    // Lists base..head as a linear review stack — the first-parent commits reachable from head
    // but not base, oldest→newest. base/head accept any ref or SHA; the returned stack carries
    // their resolved SHAs and short-sha labels (the caller overrides labels with branch names).
    Fetched<ReviewStack> LoadReviewStack(Repo repo, string baseRef, string headRef, int cap);
    // The merge-base (common-ancestor) SHA of two refs/SHAs, or null when none exists (unrelated
    // histories) or git fails. Anchors a review range's base at the divergence point.
    string? MergeBase(Repo repo, string a, string b);
    // The default review base for headRef when no explicit base is pinned: the merge-base with the
    // branch's upstream, else with the repo's default branch — carrying the ref name + kind it came
    // from (so the header can name it). Null when neither resolves.
    ResolvedReviewBase? ResolveAutoReviewBase(Repo repo, string headRef);
    bool IsAncestor(Repo repo, string maybeAncestor, string descendant);
}
