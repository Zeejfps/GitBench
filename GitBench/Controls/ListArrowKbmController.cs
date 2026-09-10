using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Input;
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
    private readonly IKeyMap _keys;
    private readonly Action<int, bool> _onMove;
    private readonly Action<bool> _onExpand;
    private readonly Action _onActivate;
    private readonly Action _onDelete;

    // Focus-traversal hooks; wired into a focus ring (e.g. local changes) so Tab leaves
    // the list for the next stop.
    public Action? OnTab { get; set; }
    public Action? OnShiftTab { get; set; }

    // Optional full-file toggle for the paired diff. Left null on lists with no diff pane (e.g.
    // the commit history list), where the key should do nothing.
    public Action? OnToggleFullFile { get; set; }

    // Optional "view the selected file in the Diff layout". Left null on lists with no diff
    // surface to jump to, where the key passes through.
    public Action? OnViewInDiff { get; set; }

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
        IKeyMap keys,
        Action<int, bool> onMove,
        Action<bool> onExpand,
        Action onActivate,
        Action onDelete)
    {
        _view = view;
        _input = input;
        _keys = keys;
        _onMove = onMove;
        _onExpand = onExpand;
        _onActivate = onActivate;
        _onDelete = onDelete;
    }

    public void TakeFocus() => _input.StealFocus(this);

    public void ReleaseFocus() => _input.Blur(this);

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;

        if (!IsOnScreen())
        {
            _input.Blur(this);
            return;
        }

        var shift = (e.Modifiers & InputModifiers.Shift) != 0;
        // Shift extends a move rather than changing which key it is.
        var moveModifiers = e.Modifiers & ~InputModifiers.Shift;

        if (RowActions?.Invoke() is { } actions)
        {
            foreach (var action in actions)
            {
                if (!action.Enabled || action.Command is not { } command) continue;
                if (!_keys.Matches(command, e.Key, e.Modifiers)) continue;
                action.Invoke();
                e.Consume();
                return;
            }
        }

        if (OnSelectAll != null && SelectAllChord.Matches(e.Key, e.Modifiers))
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
        else if (_keys.Matches(KeyCommand.ListUp, e.Key, moveModifiers))
        {
            _onMove(-1, shift);
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.ListDown, e.Key, moveModifiers))
        {
            _onMove(+1, shift);
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.ListExpand, e.Key, e.Modifiers))
        {
            _onExpand(true);
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.ListCollapse, e.Key, e.Modifiers))
        {
            _onExpand(false);
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.ListActivate, e.Key, e.Modifiers))
        {
            _onActivate();
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.ListDelete, e.Key, e.Modifiers))
        {
            _onDelete();
            e.Consume();
        }
        else if (OnToggleFullFile != null && _keys.Matches(KeyCommand.ToggleFullFile, e.Key, e.Modifiers))
        {
            OnToggleFullFile();
            e.Consume();
        }
        else if (OnViewInDiff != null && _keys.Matches(KeyCommand.ListViewInDiff, e.Key, e.Modifiers))
        {
            OnViewInDiff();
            e.Consume();
        }
    }

    private static readonly KeyGesture SelectAllChord = KeyGesture.WithPrimary(KeyboardKey.A);

    private bool IsOnScreen()
    {
        for (var view = _view; view is not null; view = view.Parent)
            if (!view.IsVisible)
                return false;

        return true;
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;
        if (_view.Position.ContainsPoint(e.Mouse.Point)) return;
        _input.Blur(this);
    }
}
