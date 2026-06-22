using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// Routes dialog-level keys to an <see cref="IDialog"/> — Esc cancels, Enter/NumpadEnter confirms —
/// mirroring how <c>KbmController</c> drives an <c>IInteractable</c>. The dialog supplies the target
/// (its <see cref="DialogState"/>); the controller never knows the concrete dialog.
/// </summary>
internal sealed class DialogKbmController : KeyboardMouseController
{
    private readonly IDialog _dialog;

    public DialogKbmController(IDialog dialog) => _dialog = dialog;

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;

        if (e.Key == KeyboardKey.Escape)
        {
            e.Consume();
            _dialog.Cancel();
        }
        else if (e.Key is KeyboardKey.Enter or KeyboardKey.NumpadEnter)
        {
            e.Consume();
            _dialog.Confirm();
        }
    }
}
