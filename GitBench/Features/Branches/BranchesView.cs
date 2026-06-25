using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// Sidebar listing local branches and remote branches (grouped per remote) and stashes as a tree —
/// branch names containing "/" split into folder nodes (e.g. "feature/login" lives inside a "feature"
/// folder). Click a branch row to select its tip commit in the history view; click a section / remote /
/// folder row to toggle collapse; double-click a branch to check it out (local branches directly;
/// remote branches with a matching local check that out, otherwise the CheckoutBranchDialog opens);
/// double-click a stash to apply it. Right-click any row for its context menu. Collapse state is
/// persisted per-repo via IRepoRegistry.
///
/// Built as a declarative tree — the same shape as the repo bar — over <see cref="BranchesViewModel"/>:
/// the VM flattens (listing, collapse-state) into <see cref="BranchesViewModel.Rows"/>, an
/// <c>Each</c> renders each row through the shared <see cref="TreeRow"/>, and the active selection
/// slides via the shared <see cref="TreeSelectionBar{TKey}"/>.
/// </summary>
internal sealed record BranchesView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<BranchesViewModel>();
        var input = ctx.Require<InputSystem>();

        // The selection bar keys on the active branch/stash row; it owns the subscription, this widget
        // owns the Derived.
        var activeKey = new Derived<BranchRowKey?>(() => BranchRowKey.From(vm.Selection.Value));
        var bar = new TreeSelectionBar<BranchRowKey>(ctx.Require<IFrameTicker>(), activeKey);

        var content = new Box
        {
            Background = Theme.Color(s => s.BranchesView.ViewBackground),
            Children =
            [
                new TreeSelectionOverlay<BranchRowKey>
                {
                    Bar = bar,
                    Child = new Switch<bool>
                    {
                        Value = vm.HasPlaceholder,
                        Case = showPlaceholder => showPlaceholder ? Placeholder(vm) : BranchList(vm, input),
                    },
                },
            ],
        };

        return new Provide<BranchesViewModel> { Value = vm, Child = content }
            .BindVm(vm)
            .Use(_ => activeKey);
    }

    private static IWidget BranchList(BranchesViewModel vm, InputSystem input) => new ScrollArea
    {
        Style = Theme.ScrollBar(),
        AutoHide = true,
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Md, Top = Spacing.Md, Bottom = Spacing.Md },
                Children =
                [
                    new Each<BranchRow>
                    {
                        Items = vm.Rows,
                        Template = new BranchListRow().WithController<BranchRowController, IBranchRowInteraction>(),
                        Gap = Spacing.Hair,
                        CrossAxis = CrossAxisAlignment.Stretch,
                    },
                ],
            },
        ],
    }.WithController(input, () => new BranchBackgroundController(vm));

    private static IWidget Placeholder(BranchesViewModel vm) => new Center
    {
        Child = new Text
        {
            Value = Prop.Bind(vm.PlaceholderText),
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.BranchesView.SectionHeaderText),
        },
    };
}
