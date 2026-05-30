using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

internal sealed class GroupSection : MultiChildView, IBind<GroupSectionViewModel>
{
    private readonly MultiChildView _headerSlot;
    private readonly FlexColumnView _rows;

    public GroupSection()
    {
        _headerSlot = new MultiChildView();
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

        _rows.BindChildren(vm.VisiblePrimaries, CreateRepoRow);
    }

    private View CreateRepoRow(Repo primary) => new RepoEntry(primary, Context!.Get<IRepoRegistry>()!);
}
