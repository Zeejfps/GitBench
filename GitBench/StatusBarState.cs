namespace GitGui;

internal sealed record StatusBarState(
    bool HasActiveRepo,
    string? RepoName,
    string? Branch,
    bool HasUpstream,
    bool IsDetached,
    int Ahead,
    int Behind)
{
    public static StatusBarState Initial { get; } = new(
        HasActiveRepo: false,
        RepoName: null,
        Branch: null,
        HasUpstream: false,
        IsDetached: false,
        Ahead: 0,
        Behind: 0);
}
