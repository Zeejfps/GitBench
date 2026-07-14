using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The strip between the working-tree pane and the commit bar: underline tabs choosing how the pane
/// presents the working tree, and — in the Diff layout — the staging progress on the trailing edge.
///
/// It sits here, not in the toolbar, because the choice only means anything for the Changes pane: the
/// toolbar is chrome shared with History, where the control has nothing to say. Underline tabs rather
/// than a pill, so it never reads as a second row of <c>Changes | History</c>. It lives outside the
/// footer slot, so a merge — which swaps the commit bar for the merge bar — can't take it away.
/// </summary>
internal sealed record WorkingChangesTabStrip : Widget
{
    private const float StripHeight = 34f;

    protected override IWidget Build(Context ctx)
    {
        var layout = ctx.Require<State<WorkingChangesLayout>>();

        return new Box
        {
            Height = StripHeight,
            Background = Theme.Color(s => s.Palette.Surface),
            BorderSize = new BorderSizeStyle { Top = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Md },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Xs,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                Tab(layout, WorkingChangesLayout.Diff, LucideIcons.ScrollText,
                                    L.T(s => s.LocalchangesLayoutDiff),
                                    L.T(s => s.LocalchangesLayoutDiffTooltip)),
                                Tab(layout, WorkingChangesLayout.Files, LucideIcons.LayoutList,
                                    L.T(s => s.LocalchangesLayoutFiles),
                                    L.T(s => s.LocalchangesLayoutFilesTooltip)),
                                new Grow { Child = new Box() },
                                new StagingProgressRow(),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private static IWidget Tab(
        State<WorkingChangesLayout> layout,
        WorkingChangesLayout value,
        string icon,
        Prop<string?> label,
        Prop<string?> tooltip)
    {
        var model = new SegmentViewModel<WorkingChangesLayout>(layout, value);
        return new UnderlineTab { Icon = icon, Label = label, Model = model }
            .WithTooltip(tooltip)
            .WithController<KbmController>();
    }
}

/// <summary>
/// One underline tab: an icon and label over an accent rule that only paints while the tab is active.
/// The rule is always laid out (a transparent bottom border when inactive) so selecting a tab never
/// shifts the text.
/// </summary>
internal sealed record UnderlineTab : Widget<ButtonState>
{
    private const float UnderlineHeight = 2f;
    private const float IconWidth = 16f;

    public required string Icon { get; init; }
    public required Prop<string?> Label { get; init; }
    public required SegmentViewModel<WorkingChangesLayout> Model { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(new Command(Model.Activate));

    protected override IWidget Build(Context ctx, ButtonState state)
    {
        Prop<uint> foreground = Theme.Color(s => Model.IsActive.Value || state.Hovered.Value
            ? s.Palette.TextPrimary
            : s.Palette.TextSecondary);

        return new Box
        {
            BorderSize = new BorderSizeStyle { Bottom = UnderlineHeight },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle
            {
                Bottom = Model.IsActive.Value ? s.Palette.Accent : 0x00000000u,
            }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Md },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Text
                                {
                                    Value = Icon,
                                    FontFamily = LucideIcons.FontFamily,
                                    FontSize = FontSize.Body,
                                    Width = IconWidth,
                                    HAlign = TextAlignment.Center,
                                    VAlign = TextAlignment.Center,
                                    Color = foreground,
                                },
                                new Text
                                {
                                    Value = Label,
                                    FontSize = FontSize.Body,
                                    HAlign = TextAlignment.Center,
                                    VAlign = TextAlignment.Center,
                                    Color = foreground,
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
