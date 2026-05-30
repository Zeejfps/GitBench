namespace GitGui;

public readonly record struct CommitSelectedMessage(Guid RepoId, string? Sha);
