using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitGui;

internal sealed class CheckoutDialogKbmController : BaseTextInputKbmController
{
    private readonly Action _onSubmit;
    private readonly Action _onCancel;

    public CheckoutDialogKbmController(TextInputView input, Action onSubmit, Action onCancel) : base(input)
    {
        _onSubmit = onSubmit;
        _onCancel = onCancel;
    }

    public CheckoutDialogKbmController(TextInputView input, ICommand onSubmit, Action onCancel)
        : this(input, onSubmit.Execute, onCancel)
    {
    }

    public CheckoutDialogKbmController(TextInputView input, IReadable<ICommand?> onSubmit, Action onCancel)
        : this(input, () => onSubmit.Value?.Execute(), onCancel)
    {
    }

    protected override void OnKeyboardKeyPressed(ref KeyboardKeyEvent e)
    {
        if (e.Key == KeyboardKey.Enter || e.Key == KeyboardKey.NumpadEnter)
        {
            e.Consume();
            _onSubmit();
            return;
        }
        if (e.Key == KeyboardKey.Escape)
        {
            e.Consume();
            _onCancel();
            return;
        }
        base.OnKeyboardKeyPressed(ref e);
    }
}