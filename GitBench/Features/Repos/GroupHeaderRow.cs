using GitBench.Controls;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

internal sealed record GroupHeaderRow : Widget
{
    public required GroupHeaderRowViewModel Model { get; init; }

    protected override View CreateView(Context ctx)
    {
        var vm = Model;
        var theme = ctx.Theme();
        var isHovered = new State<bool>(false);

        var chevron = new TextView(ctx.Canvas)
        {
            Text = ChevronFor(vm.Group.IsCollapsed),
            FontFamily = LucideIcons.FontFamily,
            FontSize = 11f,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            Width = 16,
        };
        chevron.BindTextColor(() => theme.Styles.Value.GroupHeaderRow.ChevronText);

        var nameSlot = new ContainerView();
        nameSlot.Children.BindChildren(
            () => new[] { vm.IsRenaming.Value },
            isRenaming => CreateNameContent(ctx, vm, isRenaming));

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
                        chevron,
                        new FlexItem { Grow = 1, Child = nameSlot },
                    }
                }
            }
        };
        background.BindBackgroundColor(() =>
            isHovered.Value ? theme.Styles.Value.GroupHeaderRow.BackgroundHover : theme.Styles.Value.GroupHeaderRow.BackgroundIdle);

        var root = new ContainerView { Height = 22 };
        root.Children.Add(background);

        root.UseController(ctx.Require<InputSystem>(), () => new GroupHeaderController(
            root, ctx,
            vm.Group,
            h => isHovered.Value = h,
            _ => BuildMenuItems(vm),
            () => vm.IsRenaming.Value,
            vm.ToggleCollapsed.Execute));
        return root;
    }

    private static View CreateNameContent(Context ctx, GroupHeaderRowViewModel vm, bool isRenaming)
    {
        if (isRenaming) return new GroupRenameField { Group = vm.Group }.BuildView(ctx);

        var theme = ctx.Theme();
        var name = new TextView(ctx.Canvas)
        {
            Text = vm.Group.Name,
            HorizontalTextAlignment = TextAlignment.Start,
            VerticalTextAlignment = TextAlignment.Center,
        };
        name.BindTextColor(() => theme.Styles.Value.GroupHeaderRow.NameText);
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
