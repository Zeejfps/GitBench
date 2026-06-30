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

        var resolved = ResolveBase(repo, session);
        if (resolved == null)
            throw new InvalidOperationException(_loc.Strings.Value.ReviewErrorNoBase);
        var b = resolved.Value;

        var fetched = _gitService.LoadReviewStack(repo, b.Sha, session.HeadRef, cap);
        if (fetched is not Fetched<ReviewStack>.Ok ok)
            throw new InvalidOperationException(fetched.FailureMessage ?? _loc.Strings.Value.ReviewErrorLoadFailed);

        // The base carries its ref name + kind (not a bare SHA), so the header reads
        // "origin/main → my-branch"; the head label is the pinned branch name.
        var stack = ok.Value;
        return Task.FromResult(stack with
        {
            HeadLabel = session.HeadLabel,
            BaseLabel = b.Ref,
            BaseRef = b.Ref,
            BaseKind = b.Kind,
        });
    }

    // Explicit base ⇒ merge-base(base, head) so the range starts at the divergence point (falling
    // back to the ref itself if histories are unrelated), labelled by the chosen ref. No explicit
    // base ⇒ auto resolution (upstream → default), labelled by the resolved ref.
    private ResolvedReviewBase? ResolveBase(Repo repo, ReviewSession session)
    {
        if (!string.IsNullOrEmpty(session.BaseRef))
        {
            var sha = _gitService.MergeBase(repo, session.BaseRef, session.HeadRef) ?? session.BaseRef;
            return new ResolvedReviewBase(sha, session.BaseLabel ?? session.BaseRef, ReviewBaseKind.Explicit);
        }
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
