namespace GitGui;

internal sealed record ActionsToolbarState(
    bool HasActiveRepo,
    PushStatus PushStatus,
    bool HasLocalChanges,
    bool IsPushing,
    bool IsPulling,
    bool IsFetching,
    string? Error)
{
    public static ActionsToolbarState Initial { get; } = new(
        HasActiveRepo: false,
        PushStatus: new PushStatus(null, HasUpstream: false, Ahead: 0, Behind: 0, IsDetached: false),
        HasLocalChanges: false,
        IsPushing: false,
        IsPulling: false,
        IsFetching: false,
        Error: null);
}