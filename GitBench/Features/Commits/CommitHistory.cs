using ZGF.Gui.Views;
using GitBench.App;
using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// The commit-history pane: the commits list on the left and a resizable commit details panel
/// on the right, with a draggable splitter between them.
/// </summary>
public sealed record CommitHistory : Widget
{
    protected override View CreateView(Context ctx) => new HistoryView(ctx);
}

internal sealed class HistoryView : ContainerView
{
    private const float SplitterThickness = 5f;
    private const float MinDetailsWidth = 240f;
    private const float MinCenterWidth = 320f;
    private const float DefaultDetailsWidth = 380f;

    private readonly View _commits;
    private readonly CommitDetailsView _details;
    private readonly View _splitter;
    private readonly PreferencesService? _preferences;
    private float _detailsWidth = DefaultDetailsWidth;

    public HistoryView(Context ctx)
    {
        var input = ctx.Require<InputSystem>();

        // Build the details panel first so its CommitDetailsViewModel subscribes to
        // CommitSelectedMessage before the commits panel's CommitsViewModel is constructed: the
        // latter auto-selects HEAD on its initial (synchronous) snapshot load and broadcasts that
        // selection, which the details panel must already be listening for to open on it.
        _details = new CommitDetailsView(ctx);
        _commits = new CommitsPanelWidget().BuildView(ctx);

        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(ctx.Theme(), s =>
            splitterHovered.Value ? s.HistorySplitter.Hover : s.HistorySplitter.Idle);
        splitter.UseController(input, () => new SplitterController(
            ctx,
            DragAxis.X,
            ResizeDetails,
            h => splitterHovered.Value = h));
        _splitter = splitter;

        AddChildToSelf(_commits);
        AddChildToSelf(_splitter);
        AddChildToSelf(_details);

        _preferences = ctx.Get<PreferencesService>();
        if (_preferences is not null)
            _detailsWidth = _preferences.Current.CommitDetailsWidth;
    }

    // The details panel may grow until the commits list reaches its minimum — no fixed upper cap, so a
    // wide window can give the details panel as much room as the user drags for.
    private float MaxDetailsWidthForLayout() =>
        Math.Max(MinDetailsWidth, Position.Width - SplitterThickness - MinCenterWidth);

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        var available = Math.Max(0f, pos.Width - SplitterThickness);
        var detailsWidth = Math.Clamp(_detailsWidth, MinDetailsWidth, MaxDetailsWidthForLayout());
        var centerWidth = Math.Max(MinCenterWidth, available - detailsWidth);
        if (centerWidth + detailsWidth > available)
        {
            detailsWidth = Math.Max(MinDetailsWidth, available - centerWidth);
        }
        _detailsWidth = detailsWidth;

        // Under RTL the details panel moves to the left and the commits list to the right.
        var rtl = IsRtl;
        var commitsLeft = rtl ? pos.Left + detailsWidth + SplitterThickness : pos.Left;
        var splitterLeft = rtl ? pos.Left + detailsWidth : pos.Left + centerWidth;
        var detailsLeft = rtl ? pos.Left : pos.Right - detailsWidth;

        _commits.LeftConstraint = commitsLeft;
        _commits.BottomConstraint = pos.Bottom;
        _commits.WidthConstraint = centerWidth;
        _commits.HeightConstraint = pos.Height;
        _commits.LayoutSelf();

        _splitter.LeftConstraint = splitterLeft;
        _splitter.BottomConstraint = pos.Bottom;
        _splitter.WidthConstraint = SplitterThickness;
        _splitter.HeightConstraint = pos.Height;
        _splitter.LayoutSelf();

        _details.LeftConstraint = detailsLeft;
        _details.BottomConstraint = pos.Bottom;
        _details.Width = detailsWidth;
        _details.WidthConstraint = detailsWidth;
        _details.HeightConstraint = pos.Height;
        _details.LayoutSelf();
    }

    private void ResizeDetails(float mouseDeltaX)
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
