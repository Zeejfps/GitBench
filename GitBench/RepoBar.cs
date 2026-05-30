using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

internal sealed class RepoBar : MultiChildView, IBind<RepoBarViewModel>
{
    private const int HorizontalPadding = 8;
    internal const int RowPaddingLeft = (int)TreeMetrics.BaseIndent;
    internal const int RowChevronWidth = 12;
    internal const int RowIconWidth = 16;
    internal const int RowIconGap = 6;
    // Nests a worktree/submodule one level (icon-to-icon) under its primary, matching the
    // other tree views' per-level step.
    internal const int WorktreeRowExtraIndent = (int)TreeMetrics.IndentLevel;

    private readonly FlexColumnView _sections;
    private RepoBarViewModel? _vm;

    public RepoBar()
    {
        _sections = new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };

        var scrollPane = new ScrollPane();
        scrollPane.Children.Add(_sections);
        scrollPane.UseController(_ => new ScrollPaneWheelController(scrollPane));

        var vScrollBar = ScrollBars.CreateVertical();

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
                            Children = { new AddRepoButton() },
                        },
                    }
                }
            }
        };
        bar.BindThemedBackgroundColor(s => s.RepoBar.Background);
        bar.BindThemedBorderColor(s => new BorderColorStyle { Right = s.RepoBar.RightBorder });
        AddChildToSelf(bar);

        this.UseController(ctx => new RepoBarContextMenuController(ctx, _ => BuildBackgroundMenuItems()));
        this.UseBehavior(_ => new ScrollSyncController(scrollPane, vScrollBar));
        this.UseViewModel(this);
    }

    public void Bind(RepoBarViewModel vm)
    {
        _vm = vm;
        _sections.BindChildren(
            vm.GroupSections,
            _ => new GroupSection(),
            onCreated: (section, sectionVm) => section.Bind(sectionVm));
    }

    private IReadOnlyList<RepoBarContextMenu.Item> BuildBackgroundMenuItems()
    {
        return
        [
            new RepoBarContextMenu.Item("New group", () => _vm?.NewGroup.Execute(), LucideIcons.FolderPlus),
        ];
    }
}
