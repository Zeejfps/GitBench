using GitBench.Features.Repos;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Branches;

// Single-click selects a branch/stash (or toggles a collapsible header/folder); double-click
// activates (checkout / apply); right-click opens the row's context menu. Hover and context-highlight
// feed the row's fill. Replaces the branches list's old VirtualRowList click dispatch, now per row in
// the widget tree. Double-click is detected by tick stamp, the same way the text-input and virtual-row
// controllers do (the event itself carries no click count).
internal sealed class BranchRowController : KeyboardMouseController
{
    private const int DoubleClickThresholdMs = 400;

    private readonly IBranchRowInteraction _target;
    private readonly Context _context;
    private int _lastClickTickMs;
    private bool _hasLastClick;

    public BranchRowController(IBranchRowInteraction target, Context context)
    {
        _target = target;
        _context = context;
    }

    public override void OnMouseEnter(ref MouseEnterEvent e) => _target.Hovered.Value = true;
    public override void OnMouseExit(ref MouseExitEvent e) => _target.Hovered.Value = false;

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;

        if (e.Button == MouseButton.Right && e.State == InputState.Pressed)
        {
            if (ShowMenu(e.Mouse.Point)) e.Consume();
            return;
        }

        if (e.Button != MouseButton.Left || e.State != InputState.Released) return;

        var now = Environment.TickCount;
        var isDouble = _hasLastClick && unchecked(now - _lastClickTickMs) <= DoubleClickThresholdMs;

        _target.Click();
        if (isDouble)
        {
            _target.Activate();
            _hasLastClick = false;
        }
        else
        {
            _lastClickTickMs = now;
            _hasLastClick = true;
        }
        e.Consume();
    }

    private bool ShowMenu(PointF point)
    {
        var items = _target.BuildMenuItems();
        if (items.Count == 0) return false;

        _target.ContextHighlighted.Value = true;
        var opened = RepoBarContextMenu.Show(_context, point, items);
        if (opened == null)
        {
            _target.ContextHighlighted.Value = false;
            return true;
        }
        opened.Closed += () => _target.ContextHighlighted.Value = false;
        return true;
    }
}
