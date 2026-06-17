using GitBench.Git;
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
            Gap = 2,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new GroupHeaderRow { Model = vm.HeaderVm },
                new Column<Repo>
                {
                    Gap = 2,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Items = Prop.Bind(vm.VisiblePrimaries),
                    Template = primary => new RepoEntry { Primary = primary },
                },
            ],
        }.WithController(ctx.Require<InputSystem>(), view => new GroupSectionController(view, ctx, vm.GroupId));
    }
}
