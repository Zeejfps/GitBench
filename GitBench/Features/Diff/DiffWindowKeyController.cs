using GitBench.Controls;
using GitBench.Input;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Diff;

/// <summary>
/// Window-level keyboard for the pop-out diff window, which has no file list (and therefore no
/// <see cref="ListArrowKbmController"/>) to carry the full-file toggle. Handles
/// <see cref="KeyCommand.ToggleFullFile"/> by bubbling — so hovering the diff body and pressing it
/// works — and steals focus on any click in the window so the key keeps working wherever the cursor
/// later rests. It never consumes mouse input, so the diff body's own controllers (hunk buttons,
/// scroll) still receive every click.
/// </summary>
internal sealed class DiffWindowKeyController : KeyboardMouseController
{
    private readonly View _view;
    private readonly InputSystem _input;
    private readonly IKeyMap _keys;
    private readonly Action _onToggleFullFile;

    public DiffWindowKeyController(View view, InputSystem input, IKeyMap keys, Action onToggleFullFile)
    {
        _view = view;
        _input = input;
        _keys = keys;
        _onToggleFullFile = onToggleFullFile;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;
        if (!_keys.Matches(KeyCommand.ToggleFullFile, e.Key, e.Modifiers)) return;
        _onToggleFullFile();
        e.Consume();
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;
        if (!_view.Position.ContainsPoint(e.Mouse.Point)) return;
        // Latch keyboard focus to this window's content without consuming the click.
        _input.StealFocus(this);
    }
}
