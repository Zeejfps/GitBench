namespace GitBench;

internal sealed record ActionsToolbarState(
    bool HasActiveRepo,
    PushStatus PushStatus,
    bool HasLocalChanges,
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
        PushStatus: new PushStatus(null, HasUpstream: false, Ahead: 0, Behind: 0, IsDetached: false),
        HasLocalChanges: false,
        IsPushing: false,
        IsPulling: false,
        IsFetching: false,
        OpError: null,
        ShellError: null);
}
