using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class CheckoutDialogKbmController : BaseTextInputKbmController
{
    private readonly Action _onSubmit;
    private readonly Action _onCancel;

    public CheckoutDialogKbmController(
        TextInputView input,
        InputSystem inputSystem,
        ZGF.Gui.IClipboard? clipboard,
        Action onSubmit,
        Action onCancel) : base(input, inputSystem, clipboard)
    {
        _onSubmit = onSubmit;
        _onCancel = onCancel;
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