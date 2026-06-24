using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

internal sealed record GroupSection : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<GroupSectionViewModel>();

        return new Column
        {
            Gap = Spacing.Hair,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new GroupHeaderRow { Model = vm.HeaderVm },
                new Each<RepoNodeViewModel>
                {
                    Items = vm.VisiblePrimaries,
                    Template = new RepoNode(),
                    Gap = Spacing.Hair,
                    CrossAxis = CrossAxisAlignment.Stretch,
                },
            ],
        }.WithController(ctx.Require<InputSystem>(), view => new GroupSectionController(view, ctx, vm.GroupId));
    }
}
