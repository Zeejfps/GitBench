namespace GitBench.Messages;

public readonly record struct CommitSelectedMessage(Guid RepoId, string? Sha);
