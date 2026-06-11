using GitBench.Features.Repos;

namespace GitBench.Features.Toolbar;

internal sealed record ActionsToolbarState(
    bool HasActiveRepo,
    // Cheap per-repo signals (branch / ahead / behind / detached / upstream / dirty) projected from
    // the status store. IsDirty doubles as "has local changes" for the Stash command.
    RepoStatus Status,
    bool IsPushing,
    bool IsPulling,
    bool IsFetching)
{
    public static ActionsToolbarState Initial { get; } = new(
        HasActiveRepo: false,
        Status: RepoStatus.Unknown,
        IsPushing: false,
        IsPulling: false,
        IsFetching: false);
}
