using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// The bar pinned under the diff in a review window: the selected increment's "X / Y files viewed"
/// meter on the leading edge and, on the trailing edge, the one adaptive primary action — "Mark
/// viewed → next file" within an increment, "Next increment" once it's fully viewed, "Review complete"
/// at the end. This is the explicit advance the review loop turns on. Reads the pinned
/// <see cref="ReviewWindowViewModel"/> from the build context.
/// </summary>
internal sealed record ReviewActionBar : Widget
{
    private const float BarHeight = 38f;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ReviewWindowViewModel>();

        return new Box
        {
            Height = BarHeight,
            Background = Theme.Color(s => s.Palette.Surface),
            BorderSize = new BorderSizeStyle { Top = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Lg },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Md,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Row
                                {
                                    Gap = Spacing.Sm,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Visible = Prop.Bind(() => vm.Hud.Value.HasFiles),
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
                                            Color = Theme.Color(s => s.Palette.TextMuted),
                                            VAlign = TextAlignment.Center,
                                        },
                                    ],
                                },
                                new Grow { Child = new Box() },
                                PrimaryButton(vm),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private static IWidget PrimaryButton(ReviewWindowViewModel vm) => new ButtonWidget
    {
        Style = ButtonStyle.Filled(s => s.Palette.Accent),
        Command = new Command(vm.RunPrimaryAction, vm.PrimaryActionEnabled),
        Children =
        [
            new ButtonIcon
            {
                FontSize = FontSize.Body,
                Value = Prop.Bind<string?>(() => PrimaryIcon(vm.Hud.Value.Primary)),
            },
            new ButtonLabel { Value = Prop.Bind<string?>(() => PrimaryLabel(vm.Hud.Value)) },
        ],
    }.WithController<KbmController>();

    private static string PrimaryIcon(ReviewPrimaryAction action) => action switch
    {
        ReviewPrimaryAction.ViewFile => LucideIcons.CheckSquare,
        ReviewPrimaryAction.NextIncrement => LucideIcons.ChevronRight,
        _ => LucideIcons.CircleCheck,
    };

    private static string PrimaryLabel(ReviewHud hud) => hud.Primary switch
    {
        ReviewPrimaryAction.ViewFile => hud.HasActiveFile ? "Mark viewed → next file" : "Review files",
        ReviewPrimaryAction.NextIncrement => "Next increment",
        _ => "Review complete",
    };
}
