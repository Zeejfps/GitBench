using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// The cross-repo review window's top bar: the set name and a member-count chip (whose tooltip lists
/// each member's resolved base → head) on the leading edge, and on the trailing edge the combined
/// review progress ("N / M files viewed" with a meter, or a "Review complete" badge once every file
/// across every member is viewed) plus the shortcuts button. Reads the pinned
/// <see cref="ChangeSetReviewViewModel"/> from the build context.
/// </summary>
internal sealed record ChangeSetReviewHeaderBar : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ChangeSetReviewViewModel>();

        return new Box
        {
            Height = ReviewHeaderBar.BarHeight,
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
                                new Grow { Child = SetLabel(vm) },
                                HealthStrip(vm),
                                ProgressGroup(vm),
                                HelpButton(vm),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    // The set name and a member-count chip; the chip's tooltip enumerates each member's base → head so
    // the reviewer can see, at a glance, what each repo is being compared against.
    private static IWidget SetLabel(ChangeSetReviewViewModel vm) => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = vm.Session.Name,
                FontSize = FontSize.Body,
                Color = Theme.Color(s => s.Palette.TextPrimary),
                Overflow = TextOverflow.Ellipsis,
                VAlign = TextAlignment.Center,
            },
            new ReviewBaseChip { Label = vm.RepoCountLabel }
                .WithTooltip(Prop.Bind(() =>
                {
                    _ = vm.LoadRevision.Value;
                    return string.Join("\n", vm.MemberSummaries().Select(m => $"{m.RepoKey}: {m.Detail}"));
                })),
            new Grow { Child = new Box() },
        ],
    };

    // The set health strip (Phase 6.1): a single aggregate badge summarizing per-member drift, with a
    // tooltip that enumerates each member's state. A stateful chip so the hover tooltip can attach.
    private static IWidget HealthStrip(ChangeSetReviewViewModel vm) =>
        new ChangeSetHealthChip { Vm = vm }.WithTooltip(Prop.Bind(vm.HealthTooltip));

    // The combined progress: a meter + "N / M files viewed" across every member while work remains; a
    // success badge once every file is viewed.
    private static IWidget ProgressGroup(ChangeSetReviewViewModel vm) => new Row
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

    private static IWidget HelpButton(ChangeSetReviewViewModel vm) => new ButtonWidget
    {
        Style = ButtonStyle.Bare(_ => Theme.Color(t => t.Palette.TextSecondary)),
        Command = new Command(vm.ToggleCheatsheet),
        ContentInset = new PaddingStyle { Left = Spacing.Xs, Right = Spacing.Xs },
        Children = [new ButtonLabel { Value = "?" }],
    }.WithTooltip(L.T(s => s.ReviewShortcutsTitle)).WithController<KbmController>();
}

/// <summary>
/// The set health strip's badge (Phase 6.1): a small chip carrying a status glyph (a green check when
/// every member is in sync, an amber alert when some member has drift, red when a member's branch is
/// gone) and the aggregate label. A stateful widget (<see cref="ButtonState"/>) so the header can attach
/// a hover tooltip enumerating each member's state. All three read the view model's live health, so the
/// chip re-derives as status probes land and members' ranges reload.
/// </summary>
internal sealed record ChangeSetHealthChip : Widget<ButtonState>
{
    public required ChangeSetReviewViewModel Vm { get; init; }

    protected override ButtonState CreateState(Context ctx) => new();

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Hair, Bottom = Spacing.Hair },
                Children =
                [
                    new Row
                    {
                        Gap = Spacing.Xs,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children =
                        [
                            new Text
                            {
                                FontFamily = LucideIcons.FontFamily,
                                FontSize = FontSize.Caption,
                                VAlign = TextAlignment.Center,
                                Value = Prop.Bind(() => Vm.HealthSeverity() == 0 ? LucideIcons.CircleCheck : LucideIcons.TriangleAlert),
                                Color = Theme.Color(s => Vm.HealthSeverity() switch
                                {
                                    0 => s.Status.Success,
                                    1 => s.Status.Warning,
                                    _ => s.Status.Danger,
                                }),
                            },
                            new Text
                            {
                                Value = Prop.Bind(Vm.HealthLabel),
                                FontSize = FontSize.Caption,
                                VAlign = TextAlignment.Center,
                                Color = Theme.Color(s => s.Palette.TextSecondary),
                            },
                        ],
                    },
                ],
            },
        ],
    };
}
