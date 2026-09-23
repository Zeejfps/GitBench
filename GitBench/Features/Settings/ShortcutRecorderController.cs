using GitBench.Input;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Settings;

/// <summary>
/// Takes the keyboard while a shortcut is being recorded, so the next key press — modifiers and all —
/// becomes the command's trigger instead of reaching the search box, the dialog, or the app. A
/// modifier pressed and released twice with nothing in between records a double tap. Esc cancels;
/// so does losing focus, since a click elsewhere means the user has moved on.
/// </summary>
internal sealed class ShortcutRecorderController : KeyboardMouseController
{
    private readonly KeyboardShortcutsViewModel _vm;
    private readonly InputSystem _input;
    private TapModifier? _held;
    private TapModifier? _tapped;

    public ShortcutRecorderController(KeyboardShortcutsViewModel vm, InputSystem input)
    {
        _vm = vm;
        _input = input;
        vm.Recording.Changed += OnRecordingChanged;
    }

    private void OnRecordingChanged(KeyCommand? recording)
    {
        _held = null;
        _tapped = null;
        if (recording is not null)
            _input.StealFocus(this);
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (_vm.Recording.Value is null) return;

        // Releases included: a chord's tail must not fire the command it used to be bound to.
        e.Consume();
        if (TapModifiers.Of(e.Key) is { } modifier)
        {
            RecordTap(modifier, e.State);
            return;
        }

        if (e.State != InputState.Pressed) return;
        _tapped = null;
        if (KeyGesture.IsModifierKey(e.Key)) return;

        if (e.Key == KeyboardKey.Escape)
            _vm.CancelRecording();
        else
            _vm.CommitRecording(new KeyGesture(e.Key, e.Modifiers & KeyGesture.RelevantMask));
        _input.Blur(this);
    }

    private void RecordTap(TapModifier modifier, InputState state)
    {
        if (state == InputState.Pressed)
        {
            // A second modifier joining the first makes a chord, not a tap.
            if (_held is { } held && held != modifier) _tapped = null;
            _held ??= modifier;
            return;
        }

        if (_held != modifier)
        {
            _held = null;
            _tapped = null;
            return;
        }

        _held = null;
        if (_tapped != modifier)
        {
            _tapped = modifier;
            return;
        }

        _vm.CommitRecording(new KeyTrigger.DoubleTap(modifier));
        _input.Blur(this);
    }

    public override void OnFocusLost()
    {
        if (_vm.Recording.Value is not null)
            _vm.CancelRecording();
    }
}
