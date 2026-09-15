using ZGF.Gui.Widgets;

namespace GitBench.Messages;

/// <summary>
/// Requests the app-level dialog surface to show a modal. The factory receives the close callback
/// the presenter wants invoked when the dialog is done.
/// </summary>
public readonly record struct ShowDialogMessage(Func<Action, IWidget> CreateDialog);
