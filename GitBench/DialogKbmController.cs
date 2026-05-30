using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitGui;

internal sealed class DialogKbmController : KeyboardMouseController
{
    private readonly Action _onConfirm;
    private readonly Action _onCancel;

    public DialogKbmController(Action onConfirm, Action onCancel)
    {
        _onConfirm = onConfirm;
        _onCancel = onCancel;
    }

    public DialogKbmController(Action onClose) : this(onClose, onClose)
    {
    }

    public DialogKbmController(ICommand confirm, Action onCancel)
        : this(confirm.Execute, onCancel)
    {
    }

    public DialogKbmController(IReadable<ICommand?> confirm, Action onCancel)
        : this(() => confirm.Value?.Execute(), onCancel)
    {
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;
        if (e.Key == KeyboardKey.Escape)
        {
            e.Consume();
            _onCancel();
        }
        else if (e.Key == KeyboardKey.Enter || e.Key == KeyboardKey.NumpadEnter)
        {
            e.Consume();
            _onConfirm();
        }
    }
}
