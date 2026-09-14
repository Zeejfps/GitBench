using GitBench.Features.Commits;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Tests;

// An index-operations store with nothing in flight and nothing to report, for subjects that only
// compose its state (the status store's badge) and never start a move.
internal sealed class IdleIndexOperations : IRepoIndexOperationsStore
{
    private readonly State<IndexOperations> _active = new(IndexOperations.Idle);

    public IReadable<IndexOperations> Active => _active;
    public bool HasUnseenError(Guid repoId) => false;

    public void Run(
        Repo repo,
        IReadOnlyList<string> paths,
        DiffSide toSide,
        Func<IReadOnlyList<string>, GitOutcome> work,
        Func<Strings, string> failureTitle,
        IndexMoveEffect effect = IndexMoveEffect.Index) { }

    public LandedMove? Settle(Guid repoId, IReadOnlyList<FileChange> unstaged, IReadOnlyList<FileChange> staged) => null;
}
