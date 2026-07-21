namespace GitBench.Messages;

// Broadcast when a git operation (checkout, stash apply, etc.) fails. DialogPresenter
// shows OperationErrorDialog with the operation-specific title and git's stderr block.
// An optional Recovery adds a one-click fix button when the failure is fixable in place.
public readonly record struct ShowOperationErrorMessage(string Title, string Message, OperationErrorRecovery? Recovery = null);

/// <summary>
/// A one-click fix the operation-error dialog offers in its bottom-left corner when a failure is
/// recoverable without leaving the app — e.g. unlocking a locked worktree, or deleting a stale
/// <c>*.lock</c>. <see cref="Fix"/> runs on click and returns null on success or a message
/// describing why it failed; <see cref="SuccessText"/> is shown when it succeeds.
/// </summary>
public sealed record OperationErrorRecovery(string ButtonLabel, string SuccessText, Func<string?> Fix);
