namespace GitGui;

// Broadcast when a git operation (checkout, stash apply, etc.) fails. DialogPresenter
// shows OperationErrorDialog with the operation-specific title and git's stderr block.
public readonly record struct ShowOperationErrorMessage(string Title, string Message);
