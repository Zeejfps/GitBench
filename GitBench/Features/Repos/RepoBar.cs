using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

internal sealed record RepoBar : Widget
{
    private const int HorizontalPadding = 8;
    internal const int RowPaddingLeft = (int)TreeMetrics.BaseIndent;
    internal const int RowChevronWidth = 12;
    internal const int RowIconWidth = 16;
    internal const int RowIconGap = 6;
    // Nests a worktree/submodule one level (icon-to-icon) under its primary, matching the
    // other tree views' per-level step.
    internal const int WorktreeRowExtraIndent = (int)TreeMetrics.IndentLevel;

    protected override View CreateView(Context ctx)
    {
        var vm = ctx.Require<RepoBarViewModel>();
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();

        var sections = new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };
        sections.Children.BindChildren(
            vm.GroupSections,
            s => new GroupSection { Model = s }.BuildView(ctx));

        var scrollPane = new ScrollPane();
        scrollPane.Children.Add(sections);
        scrollPane.UseController(input, () => new ScrollPaneWheelController(scrollPane));

        var vScrollBar = ScrollBars.CreateVertical(ctx);

        var scrollArea = new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new FlexItem { Grow = 1, Child = scrollPane },
                vScrollBar,
            },
        };

        var bar = new RectView
        {
            BorderSize = new BorderSizeStyle { Right = 1 },
            Children =
            {
                new FlexColumnView
                {
                    CrossAxisAlignment = CrossAxisAlignment.Stretch,
                    Children =
                    {
                        new FlexItem { Grow = 1, Child = scrollArea },
                        new PaddingView
                        {
                            Padding = new PaddingStyle
                            {
                                Left = HorizontalPadding,
                                Right = HorizontalPadding,
                                Top = HorizontalPadding,
                                Bottom = HorizontalPadding,
                            },
                            Children = { new AddRepoButton().BuildView(ctx) },
                        },
                    }
                }
            }
        };
        bar.BindBackgroundColor(() => theme.Styles.Value.RepoBar.Background);
        bar.BindBorderColor(() => new BorderColorStyle { Right = theme.Styles.Value.RepoBar.RightBorder });

        var root = new ContainerView();
        root.Children.Add(bar);

        root.UseController(input, () => new RepoBarContextMenuController(ctx, _ => BuildBackgroundMenuItems(vm)));
        root.Use(() => new ScrollSyncController(scrollPane, vScrollBar));
        root.UseViewModel(() => vm, _ => { });
        return root;
    }

    private static IReadOnlyList<RepoBarContextMenu.Item> BuildBackgroundMenuItems(RepoBarViewModel vm)
    {
        var items = new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item("New group", () => vm.NewGroup.Execute(), LucideIcons.FolderPlus),
        };
        if (vm.HasMultipleGroups)
        {
            items.Add(RepoBarContextMenu.Separator);
            items.Add(new RepoBarContextMenu.Item("Expand All", () => vm.ExpandAllGroups.Execute(), LucideIcons.ChevronDown));
            items.Add(new RepoBarContextMenu.Item("Collapse All", () => vm.CollapseAllGroups.Execute(), LucideIcons.ChevronRight));
        }
        return items;
    }
}
