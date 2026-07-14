using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Features.Diff;

/// <summary>
/// The right-aligned per-hunk action pills (Stage/Unstage/Discard) drawn over a hovered hunk,
/// shared by the single-file diff pane and the stacked review surface so their geometry and
/// hit-testing can't drift. Owns the pill layout and the measured label widths; hover bookkeeping
/// and the hunk outline stay per-host (their row models differ). <c>rowTop</c> in every call is
/// the top edge of the hunk's button row — the strip hangs above it, overlapping the row before.
/// </summary>
internal sealed class HunkButtonBar
{
    public const float ButtonHeight = 18f;
    public const float TopInset = 4f;
    private const float PaddingX = 10f;
    private const float ButtonGap = 6f;
    private const float MarginRight = 8f;

    private static readonly TextStyle LabelStyle = new()
    {
        FontSize = FontSize.Caption,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };

    private readonly ILocalizationService _loc;
    private float _stageWidth;
    private float _unstageWidth;
    private float _discardWidth;
    private bool _metricsResolved;

    public HunkButtonBar(ILocalizationService loc) => _loc = loc;

    private static readonly HunkAction[] NoActions = [];
    private static readonly HunkAction[] StageDiscard = [HunkAction.Stage, HunkAction.Discard];
    private static readonly HunkAction[] UnstageOnly = [HunkAction.Unstage];
    private static readonly HunkAction[] AllActions = [HunkAction.Stage, HunkAction.Unstage, HunkAction.Discard];

    // WorkingTree's full set is the fallback while a card's per-hunk index states are still being
    // computed — an inapplicable click lands on the VM's "nothing to …" toast.
    public static HunkAction[] ActionsFor(DiffSide side) => side switch
    {
        DiffSide.Unstaged => StageDiscard,
        DiffSide.Staged => UnstageOnly,
        DiffSide.WorkingTree => AllActions,
        _ => NoActions,
    };

    // A WorkingTree hunk's pills from its real index state: staged content can be pulled back out,
    // unstaged content can be captured or discarded. Stage and Unstage flip as regions move.
    public static HunkAction[] ActionsFor(WorkingTreeHunkState state) => (state.HasStaged, state.HasUnstaged) switch
    {
        (true, false) => UnstageOnly,
        (true, true) => AllActions,
        _ => StageDiscard,
    };

    public static HunkAction[] ActionsFor(IReadOnlyList<WorkingTreeHunkState>? states, int hunkIndex, DiffSide side)
        => side == DiffSide.WorkingTree && states != null && hunkIndex >= 0 && hunkIndex < states.Count
            ? ActionsFor(states[hunkIndex])
            : ActionsFor(side);

    public static int ButtonRowFor(HunkRowRange range) => Math.Min(range.FirstRow + 1, range.LastRow);

    /// <summary>Drops the measured label cache so the next draw re-measures in a new language.</summary>
    public void InvalidateMetrics() => _metricsResolved = false;

    public void EnsureMetrics(ICanvas c)
    {
        if (_metricsResolved) return;
        var s = _loc.Strings.Value;
        _stageWidth = c.MeasureTextWidth(s.DiffHunkStage, LabelStyle);
        _unstageWidth = c.MeasureTextWidth(s.DiffHunkUnstage, LabelStyle);
        _discardWidth = c.MeasureTextWidth(s.DiffHunkDiscard, LabelStyle);
        _metricsResolved = _stageWidth > 0;
    }

    public void Draw(
        ICanvas c,
        float rightEdge,
        float rowTop,
        HunkAction[] actions,
        HunkAction hoveredButton,
        DiffHunkButtonStyles styles,
        int z)
    {
        if (actions.Length == 0) return;

        var x = rightEdge - MarginRight - TotalWidth(actions);
        var btnBottom = rowTop - TopInset - ButtonHeight;

        foreach (var action in actions)
        {
            var width = TextWidth(action) + PaddingX * 2;
            var rect = new RectF(x, btnBottom, width, ButtonHeight);
            var bg = action == hoveredButton ? styles.BackgroundHover : styles.BackgroundIdle;

            c.DrawRect(new DrawRectInputs
            {
                Position = rect,
                Style = new RectStyle
                {
                    BackgroundColor = bg,
                    BorderColor = BorderColorStyle.All(styles.Border),
                    BorderSize = BorderSizeStyle.All(1),
                    BorderRadius = BorderRadiusStyle.All(Radius.Sm),
                },
                ZIndex = z,
            });

            LabelStyle.TextColor = styles.Text;
            c.DrawText(new DrawTextInputs
            {
                Position = rect,
                Text = Label(action),
                Style = LabelStyle,
                ZIndex = z + 1,
            });

            x += width + ButtonGap;
        }
    }

    public HunkAction HitTest(PointF point, float rightEdge, float rowTop, HunkAction[] actions)
    {
        if (actions.Length == 0) return HunkAction.None;

        var btnTop = rowTop - TopInset;
        var btnBottom = btnTop - ButtonHeight;
        if (point.Y < btnBottom || point.Y > btnTop) return HunkAction.None;

        var x = rightEdge - MarginRight - TotalWidth(actions);
        foreach (var action in actions)
        {
            var width = TextWidth(action) + PaddingX * 2;
            if (point.X >= x && point.X <= x + width) return action;
            x += width + ButtonGap;
        }
        return HunkAction.None;
    }

    private float TotalWidth(HunkAction[] actions)
    {
        var total = 0f;
        for (var i = 0; i < actions.Length; i++)
        {
            total += TextWidth(actions[i]) + PaddingX * 2;
            if (i > 0) total += ButtonGap;
        }
        return total;
    }

    private float TextWidth(HunkAction action) => action switch
    {
        HunkAction.Stage => _stageWidth,
        HunkAction.Unstage => _unstageWidth,
        HunkAction.Discard => _discardWidth,
        _ => 0f,
    };

    private string Label(HunkAction action)
    {
        var s = _loc.Strings.Value;
        return action switch
        {
            HunkAction.Stage => s.DiffHunkStage,
            HunkAction.Unstage => s.DiffHunkUnstage,
            HunkAction.Discard => s.DiffHunkDiscard,
            _ => string.Empty,
        };
    }
}
