using GitBench.Features.Repos;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Controls;

// Click → run the activate command; right-click → context menu. Used for rows that don't participate
// in drag-to-reorder (worktrees, submodules — they follow their parent). The command's CanExecute
// vetoes activation (e.g. a missing submodule has no working tree to navigate to), so a vetoed click
// is left unconsumed.
public sealed class NavigableRowController : KeyboardMouseController
{
    private readonly INavigableRow _target;
    private readonly Context _context;

    public NavigableRowController(INavigableRow target, Context context)
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
            var items = _target.BuildMenuItems();
            if (items.Count > 0)
            {
                RepoBarContextMenu.Show(_context, e.Mouse.Point, items);
                e.Consume();
            }
            return;
        }

        if (e.Button != MouseButton.Left) return;
        if (e.State != InputState.Released) return;
        if (!_target.Activate.CanExecute.Value) return;

        _target.Activate.Execute();
        e.Consume();
    }
}
