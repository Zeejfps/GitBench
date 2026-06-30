using ZGF.Gui.Views;
using GitBench.App;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Commits;

/// <summary>
/// The commit-history pane: the commits list on the left and a resizable commit details panel
/// on the right, with a draggable divider between them.
/// </summary>
public sealed record CommitHistory : Widget
{
    protected override View CreateView(Context ctx) => new HistoryView(ctx);
}

internal sealed class HistoryView : ContainerView
{
    private const float MinDetailsWidth = 240f;
    private const float MinCenterWidth = 320f;
    private const float DefaultDetailsWidth = 380f;
    private const float DividerHitWidth = 6f;
    private const float DividerThickness = 1f;

    private readonly View _commits;
    private readonly CommitDetailsView _details;
    private readonly PreferencesService? _preferences;
    private float _detailsWidth = DefaultDetailsWidth;
    private bool _dividerHovered;

    private HistorySplitterStyles _splitterStyles = ThemeStyles.Dark.HistorySplitter;

    public HistoryView(Context ctx)
    {
        var input = ctx.Require<InputSystem>();

        // Build the details panel first so its CommitDetailsViewModel subscribes to
        // CommitSelectedMessage before the commits panel's CommitsViewModel is constructed: the
        // latter auto-selects HEAD on its initial (synchronous) snapshot load and broadcasts that
        // selection, which the details panel must already be listening for to open on it.
        _details = new CommitDetailsView(ctx);
        _commits = new CommitsPanelWidget().BuildView(ctx);
        AddChildToSelf(_commits);
        AddChildToSelf(_details);
        this.UseController(input, () => new HistoryViewController(this, input));

        this.BindThemed(ctx.Theme(), s =>
        {
            _splitterStyles = s.HistorySplitter;
            SetDirty();
        });

        _preferences = ctx.Get<PreferencesService>();
        if (_preferences is not null)
            _detailsWidth = _preferences.Current.CommitDetailsWidth;
    }

    // The details panel may grow until the commits list reaches its minimum — no fixed upper cap, so a
    // wide window can give the details panel as much room as the user drags for.
    private float MaxDetailsWidthForLayout() => Math.Max(MinDetailsWidth, Position.Width - MinCenterWidth);

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        var detailsWidth = Math.Clamp(_detailsWidth, MinDetailsWidth, MaxDetailsWidthForLayout());
        var centerWidth = Math.Max(MinCenterWidth, pos.Width - detailsWidth);
        if (centerWidth + detailsWidth > pos.Width)
        {
            detailsWidth = Math.Max(MinDetailsWidth, pos.Width - centerWidth);
        }
        _detailsWidth = detailsWidth;

        // Under RTL the details panel moves to the left and the commits list to the right.
        _commits.LeftConstraint = IsRtl ? pos.Left + detailsWidth : pos.Left;
        _commits.BottomConstraint = pos.Bottom;
        _commits.WidthConstraint = centerWidth;
        _commits.HeightConstraint = pos.Height;
        _commits.LayoutSelf();

        _details.LeftConstraint = IsRtl ? pos.Left : pos.Right - detailsWidth;
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
        var clampedWidth = Math.Clamp(_detailsWidth, MinDetailsWidth, MaxDetailsWidthForLayout());
        var dividerX = IsRtl ? pos.Left + clampedWidth : pos.Right - clampedWidth;
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
        var clampedWidth = Math.Clamp(_detailsWidth, MinDetailsWidth, MaxDetailsWidthForLayout());
        var dividerX = IsRtl ? pos.Left + clampedWidth : pos.Right - clampedWidth;
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
        // Dragging right (positive delta) shrinks the right panel and grows the center; under RTL the
        // details panel is on the left, so the drag direction flips.
        if (IsRtl) mouseDeltaX = -mouseDeltaX;
        var newWidth = Math.Clamp(_detailsWidth - mouseDeltaX, MinDetailsWidth, MaxDetailsWidthForLayout());
        if (Math.Abs(newWidth - _detailsWidth) < 0.0001f) return;
        _detailsWidth = newWidth;
        _preferences?.SetCommitDetailsWidth(newWidth);
        SetDirty();
    }
}

internal sealed class HistoryViewController : KeyboardMouseController
{
    private readonly HistoryView _view;
    private readonly InputSystem _input;
    private bool _dragging;
    private float _lastDragX;

    public HistoryViewController(HistoryView view, InputSystem input)
    {
        _view = view;
        _input = input;
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
                _input.StealFocus(this);
                e.Consume();
            }
            return;
        }

        if (e.State == InputState.Released && _dragging)
        {
            _dragging = false;
            _input.Blur(this);
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
