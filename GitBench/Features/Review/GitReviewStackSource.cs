using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;

namespace GitBench.Features.Review;

/// <summary>
/// The real <see cref="IReviewStackSource"/> (Phase 4): resolves a <see cref="ReviewSession"/> into the
/// true <c>base..head</c> first-parent stack through the git range layer. The base is the session's
/// pinned <see cref="ReviewSession.BaseRef"/> when set, otherwise the merge-base with the branch's
/// upstream or the repo's default branch. Replaces <see cref="StubReviewStackSource"/> through the
/// single DI binding; the review window's GUI is unchanged. A load failure (missing repo, no
/// resolvable base, git error) is surfaced as a thrown exception per the seam contract.
/// </summary>
internal sealed class GitReviewStackSource : IReviewStackSource
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly ILocalizationService _loc;

    public GitReviewStackSource(IRepoRegistry registry, IGitService gitService, ILocalizationService loc)
    {
        _registry = registry;
        _gitService = gitService;
        _loc = loc;
    }

    public Task<ReviewStack> LoadAsync(ReviewSession session, int cap)
    {
        var repo = ResolveRepo(session.RepoId);
        if (repo == null)
            throw new InvalidOperationException(_loc.Strings.Value.ReviewErrorRepoUnavailable);

        var baseRef = ResolveBaseRef(repo, session);
        if (baseRef == null)
            throw new InvalidOperationException(_loc.Strings.Value.ReviewErrorNoBase);

        var fetched = _gitService.LoadReviewStack(repo, baseRef, session.HeadRef, cap);
        if (fetched is not Fetched<ReviewStack>.Ok ok)
            throw new InvalidOperationException(fetched.FailureMessage ?? _loc.Strings.Value.ReviewErrorLoadFailed);

        // Labels come from the pinned session (branch names); the base falls back to its short SHA.
        var stack = ok.Value;
        return Task.FromResult(stack with
        {
            HeadLabel = session.HeadLabel,
            BaseLabel = session.BaseLabel ?? stack.BaseLabel,
        });
    }

    // Explicit base ⇒ merge-base(base, head) so the range starts at the divergence point (falling
    // back to the ref itself if histories are unrelated). No explicit base ⇒ auto resolution.
    private string? ResolveBaseRef(Repo repo, ReviewSession session)
    {
        if (!string.IsNullOrEmpty(session.BaseRef))
            return _gitService.MergeBase(repo, session.BaseRef, session.HeadRef) ?? session.BaseRef;
        return _gitService.ResolveAutoReviewBase(repo, session.HeadRef);
    }

    private Repo? ResolveRepo(Guid repoId)
    {
        var active = _registry.Active.Value;
        if (active != null && active.Id == repoId) return active;
        foreach (var r in _registry.Repos)
            if (r.Id == repoId) return r;
        return null;
    }
}
