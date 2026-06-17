using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
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
        var input = ctx.Require<InputSystem>();

        var bar = new Box
        {
            BorderSize = new BorderSizeStyle { Right = 1 },
            Background = Theme.Color(s => s.RepoBar.Background),
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Right = s.RepoBar.RightBorder }),
            Children =
            [
                new Column
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new Grow
                        {
                            Child = new ScrollArea
                            {
                                Style = Theme.ScrollBar(),
                                Children =
                                [
                                    Each.Of(vm.GroupSections, new GroupSection(), gap: 2) with
                                    {
                                        CrossAxis = CrossAxisAlignment.Stretch,
                                    },
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
