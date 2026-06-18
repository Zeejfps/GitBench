using GitBench.Features.Repos;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Controls;

// Click → run the activate command; right-click → context menu. Used for rows that don't participate
// in drag-to-reorder (worktrees, submodules — they follow their parent). The command's CanExecute
// vetoes activation (e.g. a missing submodule has no working tree to navigate to), so a vetoed click
// is left unconsumed.
internal sealed class NavigableRowController : KeyboardMouseController
{
    private readonly Context _context;
    private readonly ICommand _activate;
    private readonly Action<bool> _onHoverChanged;
    private readonly Func<PointF, IReadOnlyList<RepoBarContextMenu.Item>> _buildMenuItems;

    public NavigableRowController(
        Context context,
        ICommand activate,
        Action<bool> onHoverChanged,
        Func<PointF, IReadOnlyList<RepoBarContextMenu.Item>> buildMenuItems)
    {
        _context = context;
        _activate = activate;
        _onHoverChanged = onHoverChanged;
        _buildMenuItems = buildMenuItems;
    }

    public override void OnMouseEnter(ref MouseEnterEvent e) => _onHoverChanged(true);
    public override void OnMouseExit(ref MouseExitEvent e) => _onHoverChanged(false);

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;

        if (e.Button == MouseButton.Right && e.State == InputState.Pressed)
        {
            var items = _buildMenuItems(e.Mouse.Point);
            if (items.Count > 0)
            {
                RepoBarContextMenu.Show(_context, e.Mouse.Point, items);
                e.Consume();
            }
            return;
        }

        if (e.Button != MouseButton.Left) return;
        if (e.State != InputState.Released) return;
        if (!_activate.CanExecute.Value) return;

        _activate.Execute();
        e.Consume();
    }
}
