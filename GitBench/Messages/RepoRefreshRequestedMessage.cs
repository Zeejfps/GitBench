namespace GitBench.Messages;

// Broadcast when the user explicitly asks to reload a repo's state (e.g. the Retry button
// on a failed status load). Unlike the change-driven messages, listeners must re-emit even
// if the reload lands on a result equal to the current one, so the UI visibly retries.
public readonly record struct RepoRefreshRequestedMessage(Guid RepoId);
