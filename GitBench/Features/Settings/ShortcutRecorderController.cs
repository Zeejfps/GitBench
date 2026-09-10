using GitBench.Input;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Settings;

/// <summary>
/// Takes the keyboard while a shortcut is being recorded, so the next key press — modifiers and all —
/// becomes the command's gesture instead of reaching the search box, the dialog, or the app. Esc
/// cancels; so does losing focus, since a click elsewhere means the user has moved on.
/// </summary>
internal sealed class ShortcutRecorderController : KeyboardMouseController
{
    private readonly KeyboardShortcutsViewModel _vm;
    private readonly InputSystem _input;

    public ShortcutRecorderController(KeyboardShortcutsViewModel vm, InputSystem input)
    {
        _vm = vm;
        _input = input;
        vm.Recording.Changed += OnRecordingChanged;
    }

    private void OnRecordingChanged(KeyCommand? recording)
    {
        if (recording is not null)
            _input.StealFocus(this);
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (_vm.Recording.Value is null) return;

        // Releases included: a chord's tail must not fire the command it used to be bound to.
        e.Consume();
        if (e.State != InputState.Pressed) return;
        if (KeyGesture.IsModifierKey(e.Key)) return;

        if (e.Key == KeyboardKey.Escape)
            _vm.CancelRecording();
        else
            _vm.CommitRecording(new KeyGesture(e.Key, e.Modifiers & KeyGesture.RelevantMask));
        _input.Blur(this);
    }

    public override void OnFocusLost()
    {
        if (_vm.Recording.Value is not null)
            _vm.CancelRecording();
    }
}
