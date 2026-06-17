using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Widgets;
using ZGF.Gui;
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

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<RepoBarViewModel>();
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();

        var scrollPane = new ScrollPane();
        scrollPane.Children.Add((Each.Of(vm.GroupSections, new GroupSection(), gap: 2) with
        {
            CrossAxis = CrossAxisAlignment.Stretch,
        }).BuildView(ctx));
        scrollPane.UseController(input, () => new ScrollPaneWheelController(scrollPane));

        var vScrollBar = ScrollBars.CreateVertical(ctx);

        var bar = new Box
        {
            BorderSize = new BorderSizeStyle { Right = 1 },
            Background = Prop.Bind(() => theme.Styles.Value.RepoBar.Background),
            BorderColor = Prop.Bind(() => new BorderColorStyle { Right = theme.Styles.Value.RepoBar.RightBorder }),
            Children =
            [
                new Column
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new Grow
                        {
                            Child = new Row
                            {
                                CrossAxis = CrossAxisAlignment.Stretch,
                                Children =
                                [
                                    new Grow { Child = new Raw { View = scrollPane } },
                                    new Raw { View = vScrollBar },
                                ],
                            },
                        },
                        new Box
                        {
                            Padding = PaddingStyle.All(HorizontalPadding),
                            Children = [new AddRepoButton()],
                        },
                    ],
                },
            ],
        };

        return bar
            .WithController(input, () => new RepoBarContextMenuController(ctx, _ => BuildBackgroundMenuItems(vm)))
            .Use(_ => new ScrollSyncController(scrollPane, vScrollBar))
            .BindVm(vm);
    }

    private static IReadOnlyList<RepoBarContextMenu.Item> BuildBackgroundMenuItems(RepoBarViewModel vm)
    {
        var items = new List<RepoBarContextMenu.Item>
        {
            new("New group", () => vm.NewGroup.Execute(), LucideIcons.FolderPlus),
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
