using GitBench.Controls;
using GitBench.Git;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Repos;

public sealed class RepoRowController : KeyboardMouseController, IDisposable
{
    private const float DragThresholdSq = 6f * 6f;

    private readonly View _view;
    private readonly IRepoRow _target;
    private readonly IRepoRegistry _registry;
    private readonly Context _context;
    private readonly IDragController? _dragController;
    private readonly InputSystem _inputSystem;

    private bool _pressed;
    private bool _dragging;
    private PointF _pressPoint;

    public RepoRowController(
        View view,
        IRepoRow target,
        IRepoRegistry registry,
        InputSystem inputSystem,
        Context context,
        IDragController? dragController = null)
    {
        _view = view;
        _target = target;
        _registry = registry;
        _inputSystem = inputSystem;
        _context = context;
        _dragController = dragController;

        var group = _registry.FindGroupContaining(_target.Repo.Id);
        if (group is not null)
            _dragController?.RegisterRepoRow(view, group.Id, _target.Repo.Id);
    }

    public void Dispose()
    {
        _dragController?.Unregister(_view);
        if (_pressed || _dragging)
        {
            _dragController?.CancelDrag();
            _inputSystem.Blur(this);
        }
    }

    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        if (_dragging) return;
        _target.Hovered.Value = true;
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        if (_dragging) return;
        _target.Hovered.Value = false;
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
            _target.Hovered.Value = false;
            _dragController?.StartRepoDrag(_target.Repo, e.Mouse.Point);
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
            var items = _target.BuildMenuItems();
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
                _target.Activate.Execute();
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
