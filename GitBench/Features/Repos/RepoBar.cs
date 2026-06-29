using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

internal sealed record RepoBar : Widget
{
    internal const int RowPaddingLeft = (int)TreeMetrics.BaseIndent;
    internal const int RowChevronWidth = (int)TreeMetrics.ChevronWidth;
    internal const int RowIconWidth = (int)TreeMetrics.IconWidth;
    internal const int RowIconGap = (int)TreeMetrics.ColumnGap;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<RepoBarViewModel>();
        var input = ctx.Require<InputSystem>();
        var registry = ctx.Require<IRepoRegistry>();
        // The selection bar keys on the active repo id; it owns the subscription, this widget owns the Derived.
        var activeId = new Derived<Guid?>(() => registry.Active.Value?.Id);
        var selectionBar = new TreeSelectionBar<Guid>(ctx.Require<IFrameTicker>(), activeId);

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
                        BuildHeader(ctx),
                        new Grow
                        {
                            Child = new TreeSelectionOverlay<Guid>
                            {
                                Bar = selectionBar,
                                Child = new ScrollArea
                                {
                                    Style = Theme.ScrollBar(),
                                    AutoHide = true,
                                    WheelStep = Scrolling.WheelStep,
                                    Children =
                                    [
                                        new Padding
                                        {
                                            Amount = new PaddingStyle { Left = Spacing.Md, Top = Spacing.Md, Bottom = Spacing.Md },
                                            Children =
                                            [
                                                new Each<GroupSectionViewModel>
                                                {
                                                    Items = vm.GroupSections,
                                                    Template = new GroupSection(),
                                                    Gap = Spacing.Lg,
                                                    CrossAxis = CrossAxisAlignment.Stretch,
                                                },
                                            ],
                                        },
                                    ],
                                },
                            },
                        },
                    ],
                },
            ],
        };

        return bar
            .WithController(input, () => new RepoBarContextMenuController(ctx, _ => BuildBackgroundMenuItems(ctx, vm)))
            .BindVm(vm)
            .Use(_ => activeId);
    }

    // Sticky panel header: the "Repositories" title plus the add-repo menu trigger. Distinct from the
    // group-section headers below (Title case, heavier, divider underneath) so it reads as the panel's
    // title, not another group.
    private static IWidget BuildHeader(Context ctx) => new Box
    {
        Height = 44,
        BorderSize = new BorderSizeStyle { Bottom = 1 },
        BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.RepoBar.RightBorder }),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Sm },
                Children =
                [
                    new Row
                    {
                        CrossAxis = CrossAxisAlignment.Center,
                        MainAxis = MainAxisAlignment.SpaceBetween,
                        Children =
                        [
                            new Text
                            {
                                Value = L.T(s => s.ReposPanelTitle),
                                FontSize = FontSize.Body,
                                Weight = FontWeight.Bold,
                                VAlign = TextAlignment.Center,
                                Color = Theme.Color(s => s.Palette.TextStrong),
                            },
                            new IconButtonWidget
                            {
                                Icon = LucideIcons.FolderPlus,
                                IconSize = 15f,
                                Width = 24,
                                Height = 24,
                                Surface = s => Theme.Color(t => t.HeaderActionButton.Surface(s)),
                                Foreground = s => Theme.Color(t => t.HeaderActionButton.Icon(s)),
                            }
                                .WithTooltip(L.T(s => s.ReposAddButton))
                                .WithMenuController(rect =>
                                    RepoBarContextMenu.Show(ctx, rect.BottomLeft, AddRepoMenu.Items(ctx), MenuPlacement.Below)),
                        ],
                    },
                ],
            },
        ],
    };

    private static IReadOnlyList<RepoBarContextMenu.Item> BuildBackgroundMenuItems(Context ctx, RepoBarViewModel vm)
    {
        var s = ctx.Localization().Strings.Value;
        var items = new List<RepoBarContextMenu.Item>
        {
            new(s.ReposMenuNewGroup, () => vm.NewGroup.Execute(), LucideIcons.FolderPlus),
        };
        if (vm.HasMultipleGroups)
        {
            items.Add(RepoBarContextMenu.Separator);
            items.Add(new RepoBarContextMenu.Item(s.CommonExpandAll, () => vm.ExpandAllGroups.Execute(), LucideIcons.ChevronDown));
            items.Add(new RepoBarContextMenu.Item(s.CommonCollapseAll, () => vm.CollapseAllGroups.Execute(), LucideIcons.ChevronRight));
        }
        return items;
    }
}
