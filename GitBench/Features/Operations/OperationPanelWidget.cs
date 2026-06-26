using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Operations;

/// <summary>
/// The in-progress operation panel content (rebase / cherry-pick / revert / apply / bisect): status
/// header, stopped-commit subject, progress, and the continue / skip / abort actions. Mounting and the
/// enter / exit animation are owned by the Local Changes <see cref="GitBench.Controls.FooterSlot"/>.
/// </summary>
internal sealed record OperationPanelWidget : Widget
{
    protected override IWidget Build(Context ctx) => Panel(ctx.Require<OperationViewModel>());

    private static IWidget Panel(OperationViewModel vm) => new FooterPanel
    {
        Children =
        [
            new OperationStatusHeader(),
            new Row
            {
                Gap = Spacing.Md,
                CrossAxis = CrossAxisAlignment.Center,
                Children =
                [
                    new Grow
                    {
                        Child = new Column
                        {
                            Gap = Spacing.Hair,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Show { When = vm.HasSubject, Then = () => Subject(vm) },
                                new Show { When = vm.HasProgress, Then = () => Progress(vm) },
                            ],
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
    };

    private static IWidget Progress(OperationViewModel vm) => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = L.T(s => s.OperationsStepProgress(vm.ProgressStep.Value, vm.ProgressTotal.Value)),
                Wrap = TextWrap.NoWrap,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.Palette.TextSecondary),
            },
            ProgressBar(vm),
        ],
    };

    private static IWidget ProgressBar(OperationViewModel vm) => new Box
    {
        Width = 180,
        Height = 4,
        BorderRadius = BorderRadiusStyle.All(2),
        Background = Theme.Color(s => s.Palette.BorderMuted),
        Children =
        [
            new Row
            {
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new Grow
                    {
                        Factor = Prop.Bind(vm.ProgressFraction),
                        Child = new Box
                        {
                            BorderRadius = BorderRadiusStyle.All(2),
                            Background = Theme.Color(s => s.Status.SuccessBar),
                        },
                    },
                    new Grow
                    {
                        Factor = Prop.Bind(() => 1f - vm.ProgressFraction.Value),
                        Child = Empty.Widget,
                    },
                ],
            },
        ],
    };

    private static IWidget Subject(OperationViewModel vm) => new Text
    {
        Value = L.T(s => s.OperationsStoppedCommit(vm.Subject.Value)),
        Wrap = TextWrap.NoWrap,
        Overflow = TextOverflow.Ellipsis,
        FontSize = FontSize.Caption,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(s => s.Palette.TextSecondary),
    };

    private static IWidget Spinner(OperationViewModel vm) => new Text
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

    private static IWidget Actions(OperationViewModel vm)
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
                    Visible = Prop.Bind(vm.IsSequencer),
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
                    Visible = Prop.Bind(vm.IsSequencer),
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
}
