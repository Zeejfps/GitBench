using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// The review window's top bar: the range (<c>head → base</c>) on the leading edge and, on the
/// trailing edge, the review progress ("N / M files viewed" with a meter, or a "Review complete"
/// badge once every file is viewed). The primary action lives on the active file's header in the
/// stacked diff list, not here. Reads the pinned <see cref="ReviewWindowViewModel"/> from the
/// build context.
/// </summary>
internal sealed record ReviewHeaderBar : Widget
{
    internal const float BarHeight = 40f;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ReviewWindowViewModel>();

        return new Box
        {
            Height = BarHeight,
            Background = Theme.Color(s => s.Palette.Surface),
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Lg },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Lg,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Grow { Child = BaseRange(vm, ctx) },
                                ProgressGroup(vm),
                                HelpButton(vm),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    // The comparison range, read the way the work flows: "head → base" — the branch under review
    // on the left, then the interactive base chip (ref name + chevron) it lands on. Purely a
    // display order; the diff itself stays base→head. The chip's dropdown is a searchable
    // context-menu popup (max height + scrollbar + a top search box), summoned from this
    // (secondary) window's context — its anchor is converted through the window's own
    // coordinates, so it lands under the chip regardless of which window is active.
    private static IWidget BaseRange(ReviewWindowViewModel vm, Context ctx) => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = $"{vm.Session.HeadLabel} →",
                FontSize = FontSize.Body,
                Color = Theme.Color(s => s.Palette.TextPrimary),
                Overflow = TextOverflow.Ellipsis,
                VAlign = TextAlignment.Center,
            },
            new ReviewBaseChip { Label = Prop.Bind(vm.BaseChipLabel) }
                .WithTooltip(Prop.Bind(vm.BaseTooltip))
                .WithMenuController(rect =>
                {
                    var s = ctx.Localization().Strings.Value;
                    RepoBarContextMenu.ShowSearchable(
                        ctx, rect.BottomLeft, vm.BuildBaseMenuItems(),
                        s.ReviewBaseSearchPlaceholder, s.ReviewBaseNoMatches);
                }),
            new Grow { Child = new Box() },
        ],
    };

    // The range-level progress: a meter + "N / M files viewed" while there's work left; a success
    // badge once every file is viewed.
    private static IWidget ProgressGroup(ReviewWindowViewModel vm) => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Row
            {
                Gap = Spacing.Sm,
                CrossAxis = CrossAxisAlignment.Center,
                Visible = Prop.Bind(() => vm.Hud.Value.HasFiles && !vm.Hud.Value.IsComplete),
                Children =
                [
                    new ReviewProgressMeter
                    {
                        Fraction = vm.FilesFraction,
                        Fill = Theme.Color(s => s.Status.Success),
                    },
                    new Text
                    {
                        Value = Prop.Bind(vm.FilesViewedLabel),
                        FontSize = FontSize.Caption,
                        Color = Theme.Color(s => s.Palette.TextSecondary),
                        VAlign = TextAlignment.Center,
                    },
                ],
            },
            new Row
            {
                Gap = Spacing.Xs,
                CrossAxis = CrossAxisAlignment.Center,
                Visible = Prop.Bind(() => vm.Hud.Value.IsComplete),
                Children =
                [
                    new Text
                    {
                        FontFamily = LucideIcons.FontFamily,
                        FontSize = FontSize.Body,
                        Value = LucideIcons.CircleCheck,
                        Color = Theme.Color(s => s.Status.Success),
                        VAlign = TextAlignment.Center,
                    },
                    new Text
                    {
                        Value = L.T(s => s.ReviewComplete),
                        FontSize = FontSize.Caption,
                        Color = Theme.Color(s => s.Status.Success),
                        VAlign = TextAlignment.Center,
                    },
                ],
            },
        ],
    };

    // A discoverable mouse affordance for the cheatsheet (mirrors the '?' key). No help glyph exists
    // in the icon subset, so it's a plain "?" with an explanatory tooltip.
    private static IWidget HelpButton(ReviewWindowViewModel vm) => new ButtonWidget
    {
        Style = ButtonStyle.Bare(_ => Theme.Color(t => t.Palette.TextSecondary)),
        Command = new Command(vm.ToggleCheatsheet),
        ContentInset = new PaddingStyle { Left = Spacing.Xs, Right = Spacing.Xs },
        Children = [new ButtonLabel { Value = "?" }],
    }.WithTooltip(L.T(s => s.ReviewShortcutsTitle)).WithController<KbmController>();
}
