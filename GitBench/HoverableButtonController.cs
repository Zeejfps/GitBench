using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.KeyboardModule;

namespace GitGui;

public sealed class HoverableButtonController(
    Action onClick,
    Action<bool> onHoverChanged,
    Action<bool>? onFocusChanged = null) : KeyboardMouseController
{
    private bool _isFocused;

    // Focus-traversal hooks; set by the owner when the button participates in a focus ring.
    public Action? OnTab { get; set; }
    public Action? OnShiftTab { get; set; }

    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        onHoverChanged(true);
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        onHoverChanged(false);
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Button == MouseButton.Left && e.State == InputState.Pressed)
        {
            onClick();
            e.Consume();
        }
    }

    public override void OnFocusGained()
    {
        Console.WriteLine($"[focusdbg] OnFocusGained hasHandler={onFocusChanged != null}");
        _isFocused = true;
        onFocusChanged?.Invoke(true);
    }

    public override void OnFocusLost()
    {
        _isFocused = false;
        onFocusChanged?.Invoke(false);
    }

    // Only act while actually focused — a merely-hovered button shouldn't swallow Enter
    // meant for whatever holds focus.
    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (!_isFocused) return;
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;

        if (e.Key is KeyboardKey.Enter or KeyboardKey.NumpadEnter)
        {
            onClick();
            e.Consume();
        }
        else if (e.Key == KeyboardKey.Tab && (OnTab != null || OnShiftTab != null))
        {
            if ((e.Modifiers & InputModifiers.Shift) != 0) OnShiftTab?.Invoke();
            else OnTab?.Invoke();
            e.Consume();
        }
    }
}
