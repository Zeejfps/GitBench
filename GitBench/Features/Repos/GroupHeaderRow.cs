using GitBench.Controls;
using GitBench.Features.ChangeSets;
using GitBench.Localization;
using GitBench.Messages;
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
            Height = Sizes.RowHeight,
            Background = Theme.Color(s => s.GroupHeaderRow.Background(isHovered.Value)),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Hair, Right = Spacing.Md },
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Center,
                            Gap = Spacing.Md,
                            Children =
                            [
                                new Text
                                {
                                    Value = Prop.Bind(() => ChevronFor(vm.Group.IsCollapsed.Value, Direction.IsRtl(ctx))),
                                    FontFamily = LucideIcons.FontFamily,
                                    FontSize = FontSize.Caption,
                                    HAlign = TextAlignment.Center,
                                    VAlign = TextAlignment.Center,
                                    Width = Sizes.Icon,
                                    Color = Theme.Color(s => s.GroupHeaderRow.ChevronText),
                                },
                                new Grow { Child = NameSlot(vm) },
                                AddRepoButton(ctx, vm, isHovered),
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

    // Visible-toggled rather than Show-wrapped so the button stays mounted (and its controller
    // registered) while hidden — otherwise it would unmount itself the instant the cursor reaches it.
    private static IWidget AddRepoButton(Context ctx, GroupHeaderRowViewModel vm, State<bool> isHovered) =>
        new IconButtonWidget
        {
            Icon = LucideIcons.FolderPlus,
            IconSize = 13f,
            Width = 18,
            Height = 18,
            Visible = Prop.Bind(() => isHovered.Value),
            Surface = s => Theme.Color(t => t.HeaderActionButton.Surface(s)),
            Foreground = s => Theme.Color(t => t.HeaderActionButton.Icon(s)),
        }
            .WithTooltip(L.T(s => s.ReposAddButton))
            .WithMenuController(rect =>
                RepoBarContextMenu.Show(ctx, rect.BottomLeft, AddRepoMenu.Items(ctx, vm.Group.Id)));

    private static IWidget NameSlot(GroupHeaderRowViewModel vm) => new Show
    {
        When = vm.IsRenaming,
        Then = () => new GroupRenameField { Group = vm.Group },
        Else = () => new Text
        {
            Value = Prop.Bind<string?>(() => vm.Group.Name.Value?.ToUpperInvariant()),
            FontSize = FontSize.Caption,
            HAlign = TextAlignment.Start,
            VAlign = TextAlignment.Center,
            Overflow = TextOverflow.Ellipsis,
            Color = Theme.Color(s => s.GroupHeaderRow.NameText),
        },
    };

    private static IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems(Context ctx, GroupHeaderRowViewModel vm)
    {
        var s = ctx.Localization().Strings.Value;
        var items = new List<RepoBarContextMenu.Item>
        {
            new(s.ReposAddButton, static () => { }, LucideIcons.FolderPlus,
                Submenu: AddRepoMenu.Items(ctx, vm.Group.Id)),
            new(s.ReposGroupRename, vm.BeginRename.Execute, LucideIcons.PencilLine),
        };

        // Start change set… — create a same-named branch across the group's primaries (Phase 4.1).
        // Only meaningful when the group holds two or more primaries (a set spans ≥2 repos).
        var primaries = ctx.Require<IRepoRegistry>().PrimariesOfGroup(vm.Group);
        if (primaries.Count >= 2)
            items.Add(new RepoBarContextMenu.Item(
                s.ChangesetsStartMenu,
                () => ctx.Require<IMessageBus>().Broadcast(new ShowDialogMessage(
                    onClose => new StartChangeSetDialog { Repos = primaries, OnClose = onClose })),
                LucideIcons.FolderGit2));

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
