using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;

namespace GitGui;

public sealed class RepoBarContextMenuController : KeyboardMouseController
{
    private readonly Context _context;
    private readonly Func<PointF, IReadOnlyList<RepoBarContextMenu.Item>> _buildItems;

    public RepoBarContextMenuController(Context context, Func<PointF, IReadOnlyList<RepoBarContextMenu.Item>> buildItems)
    {
        _context = context;
        _buildItems = buildItems;
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Right) return;
        if (e.State != InputState.Pressed) return;

        var anchor = e.Mouse.Point;
        var items = _buildItems(anchor);
        if (items.Count == 0) return;

        RepoBarContextMenu.Show(_context, anchor, items);
        e.Consume();
    }
}
