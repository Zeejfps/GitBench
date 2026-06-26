using GitBench.Controls;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Operations;

internal sealed record OperationPanelWidget : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<OperationViewModel>();
        return new Show
        {
            When = vm.IsActive,
            Then = () => Panel(vm),
        };
    }

    private static IWidget Panel(OperationViewModel vm) => new Box
    {
        Background = Theme.Color(s => s.CommitBar.Background),
        BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.Banner.Border }),
        BorderSize = new BorderSizeStyle { Top = 1 },
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Lg, Top = Spacing.Md, Bottom = Spacing.Md },
                Children =
                [
                    new Column
                    {
                        Gap = Spacing.Md,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children =
                        [
                            new Row
                            {
                                Gap = Spacing.Md,
                                CrossAxis = CrossAxisAlignment.Center,
                                Children =
                                [
                                    new Show { When = vm.ShowsConflictCue, Then = () => StatusIcon(vm) },
                                    new Column
                                    {
                                        Gap = Spacing.Hair,
                                        Children =
                                        [
                                            Title(vm),
                                            new Show { When = vm.ShowsConflictCue, Then = () => Detail(vm) },
                                        ],
                                    },
                                ],
                            },
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
                                                new Row
                                                {
                                                    Gap = Spacing.Md,
                                                    CrossAxis = CrossAxisAlignment.Center,
                                                    Children =
                                                    [
                                                        new Show { When = vm.HasProgress, Then = () => Progress(vm) },
                                                        new Show { When = vm.HasSubject, Then = () => Subject(vm) },
                                                    ],
                                                },
                                                new Show { When = vm.HasContext, Then = () => ContextLabel(vm) },
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
                    },
                ],
            },
        ],
    };

    private static IWidget StatusIcon(OperationViewModel vm) => new Text
    {
        Value = vm.IsConflicted.Bind(c => c ? LucideIcons.TriangleAlert : LucideIcons.CircleCheck),
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Display,
        VAlign = TextAlignment.Center,
        HAlign = TextAlignment.Center,
        Color = Theme.Color(s => vm.IsConflicted.Value ? s.Status.Warning : s.Status.Success),
    };

    private static IWidget Title(OperationViewModel vm) => new Text
    {
        Value = L.T(s => TitleFor(s, vm)),
        Wrap = TextWrap.NoWrap,
        Color = Theme.Color(s => s.Banner.Text),
    };

    private static IWidget Detail(OperationViewModel vm) => new Text
    {
        Value = L.T(s => DetailFor(s, vm)),
        Wrap = TextWrap.NoWrap,
        Overflow = TextOverflow.Ellipsis,
        FontSize = FontSize.Caption,
        Color = Theme.Color(s => s.Palette.TextSecondary),
    };

    private static IWidget Progress(OperationViewModel vm) => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = Prop.Bind(vm.ProgressLabel),
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
        Value = vm.Subject.Bind(sub => sub is null ? null : $"“{sub}”"),
        Wrap = TextWrap.NoWrap,
        Overflow = TextOverflow.Ellipsis,
        FontSize = FontSize.Caption,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(s => s.Palette.TextSecondary),
    };

    private static IWidget ContextLabel(OperationViewModel vm) => new Text
    {
        Value = Prop.Bind(vm.Context),
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

    private static string TitleFor(Strings s, OperationViewModel vm)
    {
        var op = vm.Operation.Value;
        if (op is null) return string.Empty;
        return op.Kind switch
        {
            RepoOperationState.Rebase => s.OperationsTitleRebase,
            RepoOperationState.Merge => s.OperationsTitleMerge,
            RepoOperationState.CherryPick => s.OperationsTitleCherryPick,
            RepoOperationState.Revert => s.OperationsTitleRevert,
            RepoOperationState.ApplyMailbox => s.OperationsTitleApply,
            RepoOperationState.Bisect => s.OperationsTitleBisect,
            RepoOperationState.UnmergedPaths => s.OperationsTitleUnmerged,
            _ => string.Empty,
        };
    }

    private static string DetailFor(Strings s, OperationViewModel vm)
    {
        if (vm.IsBusy.Value) return s.OperationsBannerBusyDefault;
        if (vm.IsConflicted.Value) return s.OperationsDetailConflicts(vm.ConflictCount.Value);
        return s.OperationsDetailResolved;
    }
}
