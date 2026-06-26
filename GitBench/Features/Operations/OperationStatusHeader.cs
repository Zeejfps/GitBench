using GitBench.Controls;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Operations;

/// <summary>
/// The shared status line for an in-progress repo operation — surfaces which operation is running
/// and how conflicted it currently is. Rendered by the workspace <see cref="OperationPanelWidget"/>
/// and, during a merge, by the commit bar, both reading the same <see cref="OperationViewModel"/>.
/// </summary>
internal sealed record OperationStatusHeader : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<OperationViewModel>();
        return new Row
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
                        new Row
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                Title(vm),
                                new Show { When = vm.HasContext, Then = () => ContextHeading(vm) },
                            ],
                        },
                        new Show { When = vm.ShowsConflictCue, Then = () => Detail(vm) },
                    ],
                },
            ],
        };
    }

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

    private static IWidget ContextHeading(OperationViewModel vm) => new Text
    {
        Value = vm.Context.Bind(c => c is null ? null : $"· {c}"),
        Wrap = TextWrap.NoWrap,
        Overflow = TextOverflow.Ellipsis,
        FontSize = FontSize.Caption,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(s => s.Palette.TextSecondary),
    };

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
