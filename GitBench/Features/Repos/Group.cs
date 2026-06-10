namespace GitBench.Features.Repos;

public sealed record Group(
    Guid Id,
    string Name,
    bool IsCollapsed,
    List<Guid> RepoIds);
