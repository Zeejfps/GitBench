using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

internal sealed record GroupHeaderRow : Widget
{
    public required GroupHeaderRowViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = Model;
        var isHovered = new State<bool>(false);

        var root = new Box
        {
            Height = 22,
            BorderRadius = BorderRadiusStyle.All(4),
            Background = Theme.Color(s => s.GroupHeaderRow.Background(isHovered.Value)),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = 2, Right = 8 },
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Center,
                            Gap = 8,
                            Children =
                            [
                                new Text
                                {
                                    Value = Prop.Bind(() => ChevronFor(vm.Group.IsCollapsed.Value, Direction.IsRtl(ctx))),
                                    FontFamily = LucideIcons.FontFamily,
                                    FontSize = 11f,
                                    HAlign = TextAlignment.Center,
                                    VAlign = TextAlignment.Center,
                                    Width = 16,
                                    Color = Theme.Color(s => s.GroupHeaderRow.ChevronText),
                                },
                                new Grow { Child = NameSlot(vm) },
                            ],
                        },
                    ],
                },
            ],
        };

        return root.WithController(ctx.Require<InputSystem>(), view => new GroupHeaderController(
            view, ctx,
            vm.Group,
            h => isHovered.Value = h,
            _ => BuildMenuItems(ctx, vm),
            () => vm.IsRenaming.Value,
            vm.ToggleCollapsed.Execute));
    }

    private static IWidget NameSlot(GroupHeaderRowViewModel vm) => new Show
    {
        When = vm.IsRenaming,
        Then = () => new GroupRenameField { Group = vm.Group },
        Else = () => new Text
        {
            Value = vm.Group.Name,
            HAlign = TextAlignment.Start,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.GroupHeaderRow.NameText),
        },
    };

    private static IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems(Context ctx, GroupHeaderRowViewModel vm)
    {
        var s = ctx.Localization().Strings.Value;
        var items = new List<RepoBarContextMenu.Item>
        {
            new(s.ReposGroupRename, vm.BeginRename.Execute, LucideIcons.PencilLine),
        };

        if (vm.CanDelete)
            items.Add(new RepoBarContextMenu.Item(s.ReposGroupDelete, vm.Delete.Execute, LucideIcons.Trash));

        items.Add(new RepoBarContextMenu.Item(s.CommonNewGroup, vm.NewGroup.Execute, LucideIcons.FolderPlus));

        if (vm.HasMultipleGroups)
        {
            items.Add(RepoBarContextMenu.Separator);
            items.Add(new RepoBarContextMenu.Item(s.CommonExpandAll, vm.ExpandAllGroups.Execute, LucideIcons.ChevronDown));
            items.Add(new RepoBarContextMenu.Item(s.CommonCollapseAll, vm.CollapseAllGroups.Execute, LucideIcons.ChevronRight));
        }
        return items;
    }

    private static string ChevronFor(bool isCollapsed, bool rtl) =>
        isCollapsed ? (rtl ? LucideIcons.ChevronLeft : LucideIcons.ChevronRight) : LucideIcons.ChevronDown;
}
