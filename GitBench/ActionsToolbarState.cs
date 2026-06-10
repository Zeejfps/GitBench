namespace GitBench;

internal sealed record ActionsToolbarState(
    bool HasActiveRepo,
    // Cheap per-repo signals (branch / ahead / behind / detached / upstream / dirty) projected from
    // the status store. IsDirty doubles as "has local changes" for the Stash command.
    RepoStatus Status,
    bool IsPushing,
    bool IsPulling,
    bool IsFetching,
    // Last push/pull/fetch failure on the active repo, projected from the operations store.
    string? OpError,
    // Local open-folder/open-terminal failure; transient and cleared on repo switch. Takes
    // precedence over OpError when present.
    string? ShellError)
{
    public static ActionsToolbarState Initial { get; } = new(
        HasActiveRepo: false,
        Status: RepoStatus.Unknown,
        IsPushing: false,
        IsPulling: false,
        IsFetching: false,
        OpError: null,
        ShellError: null);
}
