using GitBench.Features.Commits;
using GitBench.Features.Repos;
using GitBench.Git;

namespace GitBench.Features.Review;

/// <summary>
/// Stand-in <see cref="IReviewStackSource"/> for Phase 3: it ignores <see cref="ReviewSession.BaseRef"/>
/// and pretends the range is the last <see cref="FakeRangeSize"/> first-parent commits from HEAD, so
/// the review GUI renders a believable stack on real diffs end-to-end before the git range backend
/// (Phase 4) exists. It reuses the existing history walk (<see cref="IGitService.Load"/>) and follows
/// the first-parent chain through the returned snapshot — no new git plumbing.
/// </summary>
internal sealed class StubReviewStackSource : IReviewStackSource
{
    // The fake range size: how many first-parent commits back from HEAD the stub treats as the stack.
    private const int FakeRangeSize = 12;

    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;

    public StubReviewStackSource(IRepoRegistry registry, IGitService gitService)
    {
        _registry = registry;
        _gitService = gitService;
    }

    public Task<ReviewStack> LoadAsync(ReviewSession session, int cap)
    {
        var repo = ResolveRepo(session.RepoId);
        if (repo == null)
            return Task.FromResult(Empty(session));

        // A window wide enough that the first-parent chain stays inside it even with interleaved
        // sibling-branch commits in the topological walk.
        var limit = Math.Min(cap, FakeRangeSize);
        var fetched = _gitService.Load(repo, Math.Max(cap, limit * 6 + 64));
        if (fetched is not Fetched<CommitSnapshot>.Ok ok)
            return Task.FromResult(Empty(session));

        var snapshot = ok.Value;
        var bySha = new Dictionary<string, CommitNode>(StringComparer.Ordinal);
        foreach (var c in snapshot.Commits) bySha[c.Sha] = c;

        // Walk first-parents from HEAD, newest→oldest, up to the fake range size.
        var chain = new List<CommitNode>();
        var cur = FindHead(snapshot);
        while (cur != null && chain.Count < limit)
        {
            chain.Add(cur);
            var parentSha = cur.ParentShas.Count > 0 ? cur.ParentShas[0] : null;
            cur = parentSha != null && bySha.TryGetValue(parentSha, out var p) ? p : null;
        }

        if (chain.Count == 0)
            return Task.FromResult(Empty(session));

        // The stack lists oldest→newest; churn is left at 0 until a later phase fills it.
        chain.Reverse();
        var increments = new List<ReviewIncrement>(chain.Count);
        foreach (var n in chain)
            increments.Add(new ReviewIncrement(
                n.Sha, ShortSha(n.Sha), n.Summary, n.Author, n.When,
                FilesChanged: 0, Added: 0, Removed: 0));

        var headSha = chain[^1].Sha;
        var oldest = chain[0];
        var baseSha = oldest.ParentShas.Count > 0 ? oldest.ParentShas[0] : oldest.Sha;
        var truncated = cur != null;

        return Task.FromResult(new ReviewStack(
            session.RepoId,
            baseSha,
            headSha,
            session.BaseLabel ?? ShortSha(baseSha),
            session.HeadLabel,
            increments,
            truncated));
    }

    private static ReviewStack Empty(ReviewSession session) => new(
        session.RepoId, string.Empty, string.Empty,
        session.BaseLabel ?? "base", session.HeadLabel,
        Array.Empty<ReviewIncrement>(), Truncated: false);

    private Repo? ResolveRepo(Guid repoId)
    {
        var active = _registry.Active.Value;
        if (active != null && active.Id == repoId) return active;
        foreach (var r in _registry.Repos)
            if (r.Id == repoId) return r;
        return null;
    }

    private static CommitNode? FindHead(CommitSnapshot snapshot)
    {
        foreach (var c in snapshot.Commits)
            foreach (var badge in c.Refs)
                if (badge.IsCurrent || badge.Kind == RefKind.Head) return c;
        return snapshot.Commits.Count > 0 ? snapshot.Commits[0] : null;
    }

    private static string ShortSha(string sha)
        => string.IsNullOrEmpty(sha) ? string.Empty : (sha.Length >= 7 ? sha[..7] : sha);
}
