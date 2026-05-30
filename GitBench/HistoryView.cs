using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;

namespace GitGui;

/// <summary>
/// Lays out the commits panel on the left and a resizable commit details panel on the right,
/// with a draggable divider between them.
/// </summary>
public sealed class HistoryView : MultiChildView
{
    private const float MinDetailsWidth = 240f;
    private const float MaxDetailsWidth = 800f;
    private const float MinCenterWidth = 320f;
    private const float DefaultDetailsWidth = 380f;
    private const float DividerHitWidth = 6f;
    private const float DividerThickness = 1f;

    private readonly CommitsPanelView _commits;
    private readonly CommitDetailsView _details;
    private float _detailsWidth = DefaultDetailsWidth;
    private bool _dividerHovered;
    private PreferencesService? _preferences;

    private HistorySplitterStyles _splitterStyles = ThemeStyles.Dark.HistorySplitter;

    public HistoryView()
    {
        _commits = new CommitsPanelView();
        _details = new CommitDetailsView();
        AddChildToSelf(_commits);
        AddChildToSelf(_details);
        this.UseController(_ => new HistoryViewController(this));

        this.BindThemed(s =>
        {
            _splitterStyles = s.HistorySplitter;
            SetDirty();
        });
    }

    protected override void OnAttachedToContext(Context context)
    {
        _preferences = context.Get<PreferencesService>();
        if (_preferences is not null)
            _detailsWidth = _preferences.Current.CommitDetailsWidth;
    }

    protected override void OnDetachedFromContext(Context context)
    {
        _preferences = null;
    }

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        var detailsWidth = Math.Clamp(_detailsWidth, MinDetailsWidth, MaxDetailsWidth);
        var centerWidth = Math.Max(MinCenterWidth, pos.Width - detailsWidth);
        if (centerWidth + detailsWidth > pos.Width)
        {
            detailsWidth = Math.Max(MinDetailsWidth, pos.Width - centerWidth);
        }
        _detailsWidth = detailsWidth;

        _commits.LeftConstraint = pos.Left;
        _commits.BottomConstraint = pos.Bottom;
        _commits.WidthConstraint = centerWidth;
        _commits.HeightConstraint = pos.Height;
        _commits.LayoutSelf();

        _details.LeftConstraint = pos.Right - detailsWidth;
        _details.BottomConstraint = pos.Bottom;
        _details.Width = detailsWidth;
        _details.WidthConstraint = detailsWidth;
        _details.HeightConstraint = pos.Height;
        _details.LayoutSelf();
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        if (!_dividerHovered) return;
        var pos = Position;
        var dividerX = pos.Right - Math.Clamp(_detailsWidth, MinDetailsWidth, MaxDetailsWidth);
        var z = GetDrawZIndex();
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(dividerX - DividerHitWidth * 0.5f, pos.Bottom, DividerHitWidth, pos.Height),
            Style = new RectStyle { BackgroundColor = _splitterStyles.HoverFill },
            ZIndex = z + 999,
        });
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(dividerX - DividerThickness * 0.5f, pos.Bottom, DividerThickness, pos.Height),
            Style = new RectStyle { BackgroundColor = _splitterStyles.HoverLine },
            ZIndex = z + 1000,
        });
    }

    internal bool HitTestDivider(PointF point)
    {
        var pos = Position;
        if (point.Y < pos.Bottom || point.Y > pos.Top) return false;
        var dividerX = pos.Right - Math.Clamp(_detailsWidth, MinDetailsWidth, MaxDetailsWidth);
        return Math.Abs(point.X - dividerX) <= DividerHitWidth * 0.5f;
    }

    internal void SetDividerHovered(bool hovered)
    {
        if (_dividerHovered == hovered) return;
        _dividerHovered = hovered;
        SetDirty();
    }

    internal void ResizeDetails(float mouseDeltaX)
    {
        // Dragging right (positive delta) shrinks the right panel and grows the center.
        var pos = Position;
        var maxByCenter = pos.Width - MinCenterWidth;
        var newWidth = Math.Clamp(_detailsWidth - mouseDeltaX, MinDetailsWidth, Math.Min(MaxDetailsWidth, maxByCenter));
        if (Math.Abs(newWidth - _detailsWidth) < 0.0001f) return;
        _detailsWidth = newWidth;
        _preferences?.SetCommitDetailsWidth(newWidth);
        SetDirty();
    }
}

internal sealed class HistoryViewController : KeyboardMouseController
{
    private readonly HistoryView _view;
    private bool _dragging;
    private float _lastDragX;

    public HistoryViewController(HistoryView view)
    {
        _view = view;
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Button != MouseButton.Left) return;

        if (e.State == InputState.Pressed)
        {
            if (_view.HitTestDivider(e.Mouse.Point))
            {
                _dragging = true;
                _lastDragX = e.Mouse.Point.X;
                _view.Context.StealFocus(this);
                e.Consume();
            }
            return;
        }

        if (e.State == InputState.Released && _dragging)
        {
            _dragging = false;
            _view.Context.Blur(this);
            e.Consume();
        }
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (_dragging)
        {
            var dx = e.Mouse.Point.X - _lastDragX;
            _lastDragX = e.Mouse.Point.X;
            _view.ResizeDetails(dx);
            e.Consume();
            return;
        }
        _view.SetDividerHovered(_view.HitTestDivider(e.Mouse.Point));
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        if (!_dragging)
            _view.SetDividerHovered(false);
    }
}
