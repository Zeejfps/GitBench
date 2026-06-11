using GitBench.Git;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;

namespace GitBench.Features.Repos;

internal sealed class GroupSection : ContainerView, IBind<GroupSectionViewModel>
{
    private readonly ContainerView _headerSlot;
    private readonly FlexColumnView _rows;

    public GroupSection()
    {
        _headerSlot = new ContainerView();
        _rows = new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };

        AddChildToSelf(new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                _headerSlot,
                _rows,
            }
        });
    }

    public void Bind(GroupSectionViewModel vm)
    {
        this.UseController(ctx => new GroupSectionController(this, ctx, vm.GroupId));

        var header = new GroupHeaderRow();
        header.Bind(vm.HeaderVm);
        _headerSlot.Children.Add(header);

        _rows.Children.BindChildren(vm.VisiblePrimaries, CreateRepoRow);
    }

    private View CreateRepoRow(Repo primary) =>
        new RepoEntry(primary, this.Context!.Get<IRepoRegistry>()!, this.Context!.Get<IRepoStatusStore>()!);
}
