using GitBench.App;
using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Toolbar;

/// <summary>
/// How the Changes tab presents the working tree: file lists over a diff pane, or every diff stacked in
/// one scroll. Deliberately icons, not a labelled pill — this picks a presentation, and a second pill
/// beside the Changes / History one would read as a second set of tabs. Hidden outside Changes mode,
/// since it has nothing to say about the commit history.
/// </summary>
internal sealed record WorkingChangesLayoutToggle : Widget
{
    private const float ButtonSize = 28f;
    private const float CornerRadius = 5f;

    protected override IWidget Build(Context ctx)
    {
        var mode = ctx.Require<State<MainViewMode>>();
        var layout = ctx.Require<State<WorkingChangesLayout>>();
        var theme = ctx.Theme();

        const float innerRadius = CornerRadius - 1f;
        var pill = new Box
        {
            Height = ButtonSize,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(CornerRadius),
            BorderColor = theme.Styles.Bind(t => BorderColorStyle.All(t.ModeSwitcher.PillBorder)),
            Children =
            [
                new Row
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        Choice(layout, WorkingChangesLayout.List, LucideIcons.LayoutList,
                            L.T(s => s.LocalchangesLayoutListTooltip),
                            new BorderRadiusStyle { TopLeft = innerRadius, BottomLeft = innerRadius }),
                        new Box
                        {
                            Width = 1f,
                            Background = theme.Styles.Bind(t => t.ModeSwitcher.SegmentSeparator),
                        },
                        Choice(layout, WorkingChangesLayout.Review, LucideIcons.ScrollText,
                            L.T(s => s.LocalchangesLayoutReviewTooltip),
                            new BorderRadiusStyle { TopRight = innerRadius, BottomRight = innerRadius }),
                    ],
                },
            ],
        };

        // The pill carries its own trailing separator so both vanish together in History mode, leaving
        // the mode switcher's separator as the only one before the sync buttons.
        return new Row
        {
            Visible = Prop.Bind(() => mode.Value == MainViewMode.LocalChanges),
            CrossAxis = CrossAxisAlignment.Center,
            Children = [pill, new SeparatorSpacer()],
        };
    }

    // One icon in the pair. Reuses the mode switcher's segment colors so the active choice reads the
    // same way the Changes / History pill does, without borrowing its labelled shape.
    private static IWidget Choice(
        State<WorkingChangesLayout> layout,
        WorkingChangesLayout value,
        string icon,
        Prop<string?> tooltip,
        BorderRadiusStyle radius)
    {
        var model = new SegmentViewModel<WorkingChangesLayout>(layout, value);
        return new IconSegment
        {
            Icon = icon,
            Radius = radius,
            Size = ButtonSize,
            Model = model,
        }.WithTooltip(tooltip).WithController<KbmController>();
    }
}

/// <summary>An icon-only sibling of <see cref="Segment"/>: same active / hover chrome, a glyph instead
/// of a label.</summary>
internal sealed record IconSegment : Widget<ButtonState>
{
    public required string Icon { get; init; }
    public required BorderRadiusStyle Radius { get; init; }
    public required float Size { get; init; }
    public required SegmentViewModel<WorkingChangesLayout> Model { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(new Command(Model.Activate));

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        Width = Size + 6f,
        Height = Size,
        BorderRadius = Radius,
        Background = Theme.Color(s => s.ModeSwitcher.SegmentBackground(Model.IsActive.Value, state.Hovered.Value)),
        Children =
        [
            new Text
            {
                Value = Icon,
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize.Body,
                HAlign = TextAlignment.Center,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.ModeSwitcher.SegmentText(Model.IsActive.Value, state.Hovered.Value)),
            },
        ],
    };
}
