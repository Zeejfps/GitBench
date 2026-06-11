using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

internal sealed record GroupSection : Widget
{
    public required GroupSectionViewModel Model { get; init; }

    protected override View CreateView(Context ctx)
    {
        var vm = Model;

        var rows = new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };
        rows.Children.BindChildren(
            vm.VisiblePrimaries,
            primary => new RepoEntry { Primary = primary }.BuildView(ctx));

        var root = new ContainerView();
        root.Children.Add(new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new GroupHeaderRow { Model = vm.HeaderVm }.BuildView(ctx),
                rows,
            }
        });

        root.UseController(ctx.Require<InputSystem>(), () => new GroupSectionController(root, ctx, vm.GroupId));
        return root;
    }
}
