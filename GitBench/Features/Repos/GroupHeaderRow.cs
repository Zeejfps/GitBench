using GitBench.Controls;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.Repos;

internal sealed class GroupHeaderRow : ContainerView, IBind<GroupHeaderRowViewModel>
{
    private readonly ContainerView _nameSlot;
    private readonly TextView _chevron;
    private readonly State<bool> _isHovered = new(false);

    public GroupHeaderRow()
    {
        Height = 22;

        _chevron = new TextView(CompatUi.Canvas)
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 11f,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            Width = 16,
        };
        _chevron.BindThemedTextColor(s => s.GroupHeaderRow.ChevronText);

        _nameSlot = new ContainerView();

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Padding = new PaddingStyle { Left = 2, Right = 8 },
            Children =
            {
                new FlexRowView
                {
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Gap = 8,
                    Children =
                    {
                        _chevron,
                        new FlexItem { Grow = 1, Child = _nameSlot },
                    }
                }
            }
        };
        background.BindThemedBackgroundColor(s =>
            _isHovered.Value ? s.GroupHeaderRow.BackgroundHover : s.GroupHeaderRow.BackgroundIdle);
        AddChildToSelf(background);
    }

    public void Bind(GroupHeaderRowViewModel vm)
    {
        _chevron.Text = ChevronFor(vm.Group.IsCollapsed);

        _nameSlot.Children.BindChildren(
            () => new[] { vm.IsRenaming.Value },
            isRenaming => CreateNameContent(vm, isRenaming));

        this.UseController(ctx => new GroupHeaderController(
            this, ctx,
            vm.Group,
            h => _isHovered.Value = h,
            _ => BuildMenuItems(vm),
            () => vm.IsRenaming.Value,
            vm.ToggleCollapsed.Execute));
    }

    private View CreateNameContent(GroupHeaderRowViewModel vm, bool isRenaming)
    {
        if (isRenaming) return new GroupRenameField(vm.Group, this.Context!.Get<IRepoRegistry>()!);

        var name = new TextView(CompatUi.Canvas)
        {
            Text = vm.Group.Name,
            HorizontalTextAlignment = TextAlignment.Start,
            VerticalTextAlignment = TextAlignment.Center,
        };
        name.BindThemedTextColor(s => s.GroupHeaderRow.NameText);
        return name;
    }

    private static IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems(GroupHeaderRowViewModel vm)
    {
        var items = new List<RepoBarContextMenu.Item>
        {
            new("Rename group", vm.BeginRename.Execute, LucideIcons.PencilLine),
        };

        if (vm.CanDelete)
            items.Add(new RepoBarContextMenu.Item("Delete group", vm.Delete.Execute, LucideIcons.Trash));

        items.Add(new RepoBarContextMenu.Item("New group", vm.NewGroup.Execute, LucideIcons.FolderPlus));

        if (vm.HasMultipleGroups)
        {
            items.Add(RepoBarContextMenu.Separator);
            items.Add(new RepoBarContextMenu.Item("Expand All", vm.ExpandAllGroups.Execute, LucideIcons.ChevronDown));
            items.Add(new RepoBarContextMenu.Item("Collapse All", vm.CollapseAllGroups.Execute, LucideIcons.ChevronRight));
        }
        return items;
    }

    private static string ChevronFor(bool isCollapsed) => isCollapsed ? LucideIcons.ChevronRight : LucideIcons.ChevronDown;
}
