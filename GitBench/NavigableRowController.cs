using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.KeyboardModule;

namespace GitGui;

// Click → activate; right-click → context menu. Used for rows that don't participate in
// drag-to-reorder (worktrees, submodules — they follow their parent). The optional
// canActivate predicate lets the caller veto activation (e.g. SubmoduleRow on a missing
// submodule has no working tree to navigate to).
internal sealed class NavigableRowController : KeyboardMouseController
{
    private readonly Context _context;
    private readonly Guid _id;
    private readonly IRepoRegistry _registry;
    private readonly Action<bool> _onHoverChanged;
    private readonly Func<PointF, IReadOnlyList<RepoBarContextMenu.Item>> _buildMenuItems;
    private readonly Func<bool>? _canActivate;

    public NavigableRowController(
        Context context,
        Guid id,
        IRepoRegistry registry,
        Action<bool> onHoverChanged,
        Func<PointF, IReadOnlyList<RepoBarContextMenu.Item>> buildMenuItems,
        Func<bool>? canActivate = null)
    {
        _context = context;
        _id = id;
        _registry = registry;
        _onHoverChanged = onHoverChanged;
        _buildMenuItems = buildMenuItems;
        _canActivate = canActivate;
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

        if (_canActivate is not null && !_canActivate()) return;

        _registry.SetActive(_id);
        e.Consume();
    }
}
