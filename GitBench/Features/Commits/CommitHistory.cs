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
    private const float SqueezedMinShare = 0.35f;

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

    // Each side keeps its minimum while there's room for both; in a pane too narrow for that, the
    // minimums shrink to a share of the width so the split still fits and the splitter stays draggable.
    private (float Min, float Max) DetailsWidthRange(float available)
    {
        var minDetails = Math.Min(MinDetailsWidth, available * SqueezedMinShare);
        var minCenter = Math.Min(MinCenterWidth, available * SqueezedMinShare);
        return (minDetails, Math.Max(minDetails, available - minCenter));
    }

    private float AvailableWidth => Math.Max(0f, Position.Width - SplitterThickness);

    private float EffectiveDetailsWidth(float available)
    {
        var (min, max) = DetailsWidthRange(available);
        return Math.Clamp(_detailsWidth, min, max);
    }

    protected override void OnLayoutChildren()
    {
        var pos = Position;
        var available = AvailableWidth;
        var detailsWidth = EffectiveDetailsWidth(available);
        var centerWidth = available - detailsWidth;

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
        var available = AvailableWidth;
        var current = EffectiveDetailsWidth(available);
        var (min, max) = DetailsWidthRange(available);
        var newWidth = Math.Clamp(current - mouseDeltaX, min, max);
        if (Math.Abs(newWidth - current) < 0.0001f) return;
        _detailsWidth = newWidth;
        _preferences?.Update(p => p with { CommitDetailsWidth = newWidth });
        SetDirty();
    }
}
