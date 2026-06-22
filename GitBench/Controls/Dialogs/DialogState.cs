using System.Diagnostics.CodeAnalysis;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// The interaction surface a <see cref="DialogKbmController"/> drives — the dialog analogue of
/// <c>IInteractable</c>: Esc routes to <see cref="Cancel"/>, Enter to <see cref="Confirm"/>. A dialog
/// widget owns a <see cref="DialogState"/> and exposes it as its <see cref="IDialog"/>, so the
/// controller routes keys to the dialog's actions without the dialog wiring the controller by hand.
/// </summary>
internal interface IDialog
{
    void Confirm();
    void Cancel();
}

/// <summary>
/// Backs a dialog's keyboard handling: <see cref="Cancel"/> closes (Esc) and <see cref="Confirm"/>
/// runs the primary action (Enter) — defaulting to close for info dialogs that have no distinct
/// confirm action. A plain leaf state, so it needs no disposal; it falls out of scope with the view.
/// </summary>
internal sealed class DialogState : IDialog
{
    private readonly Action _cancel;
    private readonly Action _confirm;

    public DialogState(Action cancel, Action? confirm = null)
    {
        _cancel = cancel;
        _confirm = confirm ?? cancel;
    }

    public void Confirm() => _confirm();
    public void Cancel() => _cancel();
}

internal static class DialogControllerExtensions
{
    /// <summary>
    /// Attaches a DI-built <typeparamref name="TController"/> to a dialog widget, injecting the widget's
    /// <see cref="DialogState"/> as the controller's <see cref="IDialog"/> target — the dialog analogue
    /// of <c>checkbox.WithController&lt;KbmController&gt;()</c>. The parent calls it on the dialog widget.
    /// </summary>
    public static IWidget WithController<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TController>(
        this IWidget<IDialog> widget)
        where TController : class, IKeyboardMouseController =>
        widget.WithController<TController, IDialog>();
}
