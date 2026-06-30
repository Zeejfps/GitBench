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
/// The review window's top bar: the range (<c>base → head</c>) on the leading edge, then the increment
/// nav cluster (<c>‹ Increment N of M ›</c>) and the stack-level progress meter ("N / M reviewed", or a
/// "Review complete" badge once every increment is done) on the trailing edge. Reads the pinned
/// <see cref="ReviewWindowViewModel"/> from the build context.
/// </summary>
internal sealed record ReviewHeaderBar : Widget
{
    private const float BarHeight = 40f;

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
                                new ReviewModeToggle(),
                                NavCluster(vm),
                                ProgressGroup(vm),
                                HelpButton(vm),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    // The comparison range: an interactive base chip (ref name + chevron) that opens the base
    // dropdown, then "→ head". The dropdown is RepoBarContextMenu's popup, summoned from this
    // (secondary) window's context — its anchor is converted through the window's own coordinates, so
    // it lands under the chip regardless of which window is active.
    private static IWidget BaseRange(ReviewWindowViewModel vm, Context ctx) => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new ReviewBaseChip { Label = Prop.Bind(vm.BaseChipLabel) }
                .WithTooltip(Prop.Bind(vm.BaseTooltip))
                .WithMenuController(rect => RepoBarContextMenu.Show(ctx, rect.BottomLeft, vm.BuildBaseMenuItems())),
            new Grow
            {
                Child = new Text
                {
                    Value = $"→ {vm.Session.HeadLabel}",
                    FontSize = FontSize.Body,
                    Color = Theme.Color(s => s.Palette.TextPrimary),
                    Overflow = TextOverflow.Ellipsis,
                    VAlign = TextAlignment.Center,
                },
            },
        ],
    };

    // ‹ Increment N of M › — the bracket buttons turn the position readout into sequential nav.
    private static IWidget NavCluster(ReviewWindowViewModel vm) => new Row
    {
        Gap = Spacing.Xs,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            NavButton(LucideIcons.ChevronLeft, vm.SelectPrevIncrement, vm.CanSelectPrev),
            new Text
            {
                Value = Prop.Bind(vm.IncrementLabel),
                FontSize = FontSize.Caption,
                Color = Theme.Color(s => s.Palette.TextSecondary),
                VAlign = TextAlignment.Center,
            },
            NavButton(LucideIcons.ChevronRight, vm.SelectNextIncrement, vm.CanSelectNext),
        ],
    };

    private static IWidget NavButton(string glyph, Action onClick, IReadable<bool> canExec) =>
        new ButtonWidget
        {
            Style = ButtonStyle.Bare(state => Theme.Color(t =>
                state.Enabled.Value ? t.Palette.TextSecondary : t.Palette.TextDisabled)),
            Command = new Command(onClick, canExec),
            ContentInset = new PaddingStyle { Left = Spacing.Xs, Right = Spacing.Xs },
            Children = [new ButtonIcon { Value = glyph, FontSize = FontSize.Body }],
        }.WithController<KbmController>();

    // The stack-level progress: a meter + "N / M reviewed" while there's work left; a success badge
    // once every increment is reviewed.
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
                Visible = Prop.Bind(() => !vm.Hud.Value.IsComplete),
                Children =
                [
                    new ReviewProgressMeter
                    {
                        Fraction = vm.IncrementsFraction,
                        Fill = Theme.Color(s => s.Palette.Accent),
                    },
                    new Text
                    {
                        Value = Prop.Bind(vm.ReviewedLabel),
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
