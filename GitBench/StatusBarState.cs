namespace GitBench;

internal sealed record StatusBarState(
    bool HasActiveRepo,
    string? RepoName,
    string? Branch,
    bool HasUpstream,
    bool IsDetached,
    int Ahead,
    int Behind,
    string? IdentityText = null,
    bool IdentityIsWarning = false)
{
    public static StatusBarState Initial { get; } = new(
        HasActiveRepo: false,
        RepoName: null,
        Branch: null,
        HasUpstream: false,
        IsDetached: false,
        Ahead: 0,
        Behind: 0,
        IdentityText: null,
        IdentityIsWarning: false);
}
