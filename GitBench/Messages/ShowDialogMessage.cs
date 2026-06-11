using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Messages;

/// <summary>
/// Requests the app-level dialog surface to show a modal. The factory receives the build
/// context of the window presenting the dialog, so widget-based dialogs wire to the right
/// canvas and input system. Legacy view-based dialogs ignore the context (they construct
/// through the compat layer); migrate them by broadcasting a widget factory instead.
/// </summary>
public readonly record struct ShowDialogMessage
{
    public Func<Context, Action, View> CreateDialog { get; }

    public ShowDialogMessage(Func<Action, View> createDialog)
    {
        CreateDialog = (_, onClose) => createDialog(onClose);
    }

    public ShowDialogMessage(Func<Action, IWidget> createDialog)
    {
        CreateDialog = (ctx, onClose) => createDialog(onClose).BuildView(ctx);
    }
}
