using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Controls;

/// <summary>
/// Arrow-key navigation for a vertical row list. Lives on the owning view and takes
/// focus when a row is clicked so Up and Down move the selection. Left/Right expand or
/// collapse (tree lists), Enter activates, Delete removes; Shift is forwarded to the
/// move callback for range extension where the list supports it. Consumers that don't
/// need an action pass a no-op. Releases focus on a click outside the view so text
/// inputs and other controllers can claim it.
///
/// Shared by the local-changes file lists (<see cref="LocalChangesContentView"/>), the
/// commit-details file list (<see cref="CommitDetailsView"/>), and the commit history
/// list (<see cref="CommitsView"/>).
/// </summary>
internal sealed class ListArrowKbmController : KeyboardMouseController
{
    private readonly View _view;
    private readonly InputSystem _input;
    private readonly Action<int, bool> _onMove;
    private readonly Action<bool> _onExpand;
    private readonly Action _onActivate;
    private readonly Action _onDelete;

    // Focus-traversal hooks; wired into a focus ring (e.g. local changes) so Tab leaves
    // the list for the next stop.
    public Action? OnTab { get; set; }
    public Action? OnShiftTab { get; set; }

    // Optional "F" hotkey for the paired diff's full-file toggle. Left null on lists with no
    // diff pane (e.g. the commit history list), where F should do nothing.
    public Action? OnToggleFullFile { get; set; }

    // Optional Ctrl/Cmd+A "select all rows". Left null on single-select lists, where the key
    // passes through.
    public Action? OnSelectAll { get; set; }

    // Per-row action shortcuts for the *selected* row, consulted before the built-in navigation
    // keys. Returns the actions for whatever row is currently selected (empty when nothing is).
    // Left null on lists with no row actions. The same list backs the row's context-menu hints, so
    // a key and its menu shortcut can't drift.
    public Func<IReadOnlyList<RowAction>>? RowActions { get; set; }

    public ListArrowKbmController(
        View view,
        InputSystem input,
        Action<int, bool> onMove,
        Action<bool> onExpand,
        Action onActivate,
        Action onDelete)
    {
        _view = view;
        _input = input;
        _onMove = onMove;
        _onExpand = onExpand;
        _onActivate = onActivate;
        _onDelete = onDelete;
    }

    public void TakeFocus() => _input.StealFocus(this);

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;

        var shift = (e.Modifiers & InputModifiers.Shift) != 0;

        if (RowActions?.Invoke() is { } actions)
        {
            foreach (var action in actions)
            {
                if (!action.Enabled || action.Gesture is not { } gesture) continue;
                if (!gesture.Matches(e.Key, e.Modifiers)) continue;
                action.Invoke();
                e.Consume();
                return;
            }
        }

        if (e.Key == KeyboardKey.A
            && (e.Modifiers & (InputModifiers.Control | InputModifiers.Super)) != 0
            && OnSelectAll != null)
        {
            OnSelectAll();
            e.Consume();
        }
        else if (e.Key == KeyboardKey.Tab && (OnTab != null || OnShiftTab != null))
        {
            if (shift) OnShiftTab?.Invoke();
            else OnTab?.Invoke();
            e.Consume();
        }
        else if (e.Key == KeyboardKey.UpArrow)
        {
            _onMove(-1, shift);
            e.Consume();
        }
        else if (e.Key == KeyboardKey.DownArrow)
        {
            _onMove(+1, shift);
            e.Consume();
        }
        else if (e.Key == KeyboardKey.RightArrow)
        {
            _onExpand(true);
            e.Consume();
        }
        else if (e.Key == KeyboardKey.LeftArrow)
        {
            _onExpand(false);
            e.Consume();
        }
        else if (e.Key is KeyboardKey.Enter or KeyboardKey.NumpadEnter)
        {
            _onActivate();
            e.Consume();
        }
        else if (e.Key == KeyboardKey.Delete)
        {
            _onDelete();
            e.Consume();
        }
        else if (e.Key == KeyboardKey.F && OnToggleFullFile != null)
        {
            OnToggleFullFile();
            e.Consume();
        }
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;
        if (_view.Position.ContainsPoint(e.Mouse.Point)) return;
        _input.Blur(this);
    }
}
