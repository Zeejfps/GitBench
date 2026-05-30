using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.KeyboardModule;

namespace GitGui;

public sealed class GroupHeaderController : KeyboardMouseController, IDisposable
{
    private const float DragThresholdSq = 6f * 6f;

    private readonly View _view;
    private readonly Context _context;
    private readonly Group _group;
    private readonly Action<bool> _onHoverChanged;
    private readonly Func<PointF, IReadOnlyList<RepoBarContextMenu.Item>> _buildMenuItems;
    private readonly Func<bool> _isRenaming;
    private readonly Action _onToggleCollapsed;

    private readonly IDragController? _dragController;
    private readonly InputSystem _inputSystem;

    private bool _pressed;
    private bool _dragging;
    private PointF _pressPoint;

    public GroupHeaderController(
        View view,
        Context context,
        Group group,
        Action<bool> onHoverChanged,
        Func<PointF, IReadOnlyList<RepoBarContextMenu.Item>> buildMenuItems,
        Func<bool> isRenaming,
        Action onToggleCollapsed)
    {
        _view = view;
        _context = context;
        _group = group;
        _onHoverChanged = onHoverChanged;
        _buildMenuItems = buildMenuItems;
        _isRenaming = isRenaming;
        _onToggleCollapsed = onToggleCollapsed;

        _dragController = context.Get<IDragController>();
        _inputSystem = context.Get<InputSystem>()!;
        _dragController?.RegisterGroupHeader(view, _group.Id);
    }

    public void Dispose()
    {
        _dragController?.Unregister(_view);
        if (_pressed || _dragging) _dragController?.CancelDrag();
    }

    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        if (_dragging) return;
        _onHoverChanged(true);
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        if (_dragging) return;
        _onHoverChanged(false);
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (!_pressed) return;

        if (!_dragging)
        {
            var dx = e.Mouse.Point.X - _pressPoint.X;
            var dy = e.Mouse.Point.Y - _pressPoint.Y;
            if (dx * dx + dy * dy < DragThresholdSq) return;

            _dragging = true;
            _onHoverChanged(false);
            _dragController?.StartGroupDrag(_group, e.Mouse.Point);
            e.Consume();
            return;
        }

        _dragController?.UpdateDrag(e.Mouse.Point);
        e.Consume();
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;

        if (e.Button == MouseButton.Right && e.State == InputState.Pressed)
        {
            if (_dragging) return;
            var items = _buildMenuItems(e.Mouse.Point);
            if (items.Count > 0)
            {
                RepoBarContextMenu.Show(_context, e.Mouse.Point, items);
                e.Consume();
            }
            return;
        }

        if (e.Button != MouseButton.Left) return;

        if (e.State == InputState.Pressed)
        {
            if (_isRenaming()) return;
            _pressed = true;
            _dragging = false;
            _pressPoint = e.Mouse.Point;
            _inputSystem.StealFocus(this);
            e.Consume();
            return;
        }

        if (e.State == InputState.Released)
        {
            if (!_pressed) return;
            _pressed = false;
            if (_dragging)
            {
                _dragging = false;
                _dragController?.CompleteDrag();
            }
            else
            {
                _onToggleCollapsed();
            }
            _inputSystem.Blur(this);
            e.Consume();
        }
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (!_dragging) return;
        if (e.State != InputState.Pressed) return;
        if (e.Key != KeyboardKey.Escape) return;
        _dragging = false;
        _pressed = false;
        _dragController?.CancelDrag();
        _inputSystem.Blur(this);
        e.Consume();
    }

    public override void OnFocusLost()
    {
        if (_dragging)
        {
            _dragController?.CancelDrag();
            _dragging = false;
        }
        _pressed = false;
    }
}
