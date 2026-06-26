using GitBench.Controls;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Operations;

/// <summary>
/// Footer action panel pinned to the bottom of the workspace while the repo is mid-operation.
/// Shows the stopped commit and the Continue / Skip / Abort actions where the conflict-resolution
/// work happens, leaving <see cref="OperationBannerWidget"/> up top as a calm status strip. Both
/// read the shared <see cref="OperationBannerViewModel"/>, so a running action spins in both.
/// </summary>
internal sealed record OperationPanelWidget : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<OperationBannerViewModel>();
        return new Show
        {
            When = vm.IsActive,
            Then = () => Panel(vm),
        };
    }

    private static IWidget Panel(OperationBannerViewModel vm) => new Box
    {
        Background = Theme.Color(s => s.CommitBar.Background),
        BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.CommitBar.TopBorder }),
        BorderSize = new BorderSizeStyle { Top = 1 },
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Lg, Top = Spacing.Md, Bottom = Spacing.Md },
                Children =
                [
                    new Row
                    {
                        Gap = Spacing.Md,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children =
                        [
                            new Grow
                            {
                                Child = new Show
                                {
                                    When = vm.HasSubject,
                                    Then = () => Subject(vm),
                                },
                            },
                            new Show
                            {
                                When = vm.IsBusy,
                                Then = () => Spinner(vm),
                                Else = () => Actions(vm),
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static IWidget Subject(OperationBannerViewModel vm) => new Text
    {
        Value = vm.Subject.Bind(sub => sub is null ? null : $"“{sub}”"),
        Wrap = TextWrap.NoWrap,
        Overflow = TextOverflow.Ellipsis,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(s => s.Palette.TextSecondary),
    };

    private static IWidget Spinner(OperationBannerViewModel vm) => new Text
    {
        Value = LucideIcons.Loader,
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Heading,
        VAlign = TextAlignment.Center,
        HAlign = TextAlignment.Center,
        Width = 20,
        Color = Theme.Color(s => s.Palette.TextSecondary),
        Rotation = Prop.Bind(vm.BusyRotation),
    };

    private static IWidget Actions(OperationBannerViewModel vm)
    {
        var continueStyle = ButtonStyle.Filled(s => s.Status.SuccessBar);
        var skipStyle = ButtonStyle.Outline(s => s.Palette.TextSecondary);
        var abortStyle = ButtonStyle.Outline(s => s.Status.DangerBar);
        return new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new ButtonWidget
                {
                    Style = continueStyle,
                    Command = vm.Continue,
                    Visible = Prop.Bind(() => SupportsContinue(vm.OperationState.Value)),
                    Children =
                    [
                        new ButtonIcon { Value = LucideIcons.ChevronsRight },
                        new ButtonLabel { Value = L.T(s => s.CommonContinue) },
                    ],
                }.WithController<KbmController>(),

                new ButtonWidget
                {
                    Style = skipStyle,
                    Command = vm.Skip,
                    Visible = Prop.Bind(() => SupportsSkip(vm.OperationState.Value)),
                    Children = [new ButtonLabel { Value = L.T(s => s.CommonSkip) }],
                }.WithController<KbmController>(),

                new ButtonWidget
                {
                    Style = abortStyle,
                    Command = vm.Abort,
                    Children =
                    [
                        new ButtonIcon { Value = LucideIcons.X },
                        new ButtonLabel { Value = L.T(s => s.CommonAbort) },
                    ],
                }.WithController<KbmController>(),
            ],
        };
    }

    private static bool SupportsContinue(RepoOperationState state) => state switch
    {
        RepoOperationState.Rebase => true,
        RepoOperationState.CherryPick => true,
        RepoOperationState.Revert => true,
        RepoOperationState.ApplyMailbox => true,
        _ => false,
    };

    private static bool SupportsSkip(RepoOperationState state) => state switch
    {
        RepoOperationState.Rebase => true,
        RepoOperationState.CherryPick => true,
        RepoOperationState.Revert => true,
        RepoOperationState.ApplyMailbox => true,
        _ => false,
    };
}
