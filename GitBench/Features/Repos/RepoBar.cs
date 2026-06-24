using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.ContextMenu;
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
                                AutoHide = true,
                                Children =
                                [
                                    new Padding
                                    {
                                        Amount = new PaddingStyle { Left = 8, Top = 8, Bottom = 8 },
                                        Children =
                                        [
                                            new Each<GroupSectionViewModel>
                                            {
                                                Items = vm.GroupSections,
                                                Template = new GroupSection(),
                                                Gap = 12,
                                                CrossAxis = CrossAxisAlignment.Stretch,
                                            },
                                        ],
                                    },
                                ],
                            },
                        },
                        new Padding
                        {
                            Amount = PaddingStyle.All(HorizontalPadding),
                            // Anchor the menu at the button's top edge and grow upward — it sits at the
                            // very bottom of the sidebar, so a downward menu would spill off-screen.
                            Children =
                            [
                                new AddRepoButton().WithMenuController(rect =>
                                    RepoBarContextMenu.Show(ctx, rect.TopLeft, AddRepoMenuItems(ctx), MenuPlacement.Above)),
                            ],
                        },
                    ],
                },
            ],
        };

        return bar
            .WithController(input, () => new RepoBarContextMenuController(ctx, _ => BuildBackgroundMenuItems(ctx, vm)))
            .BindVm(vm);
    }

    private static IReadOnlyList<RepoBarContextMenu.Item> AddRepoMenuItems(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        return
        [
            new(s.ReposMenuOpenFromFolder, () => OpenFromFolder(ctx), Icon: LucideIcons.FolderOpen),
            new(s.ReposMenuCloneRepository, () => ShowCloneDialog(ctx), Icon: LucideIcons.FolderGit2),
        ];
    }

    private static void OpenFromFolder(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var path = ctx.Get<IPlatformShell>()?.PickFolder(s.ReposPickerOpenRepository);
        if (string.IsNullOrEmpty(path)) return;
        if (ctx.Get<IRepoRegistry>()?.Open(path) == OpenRepoOutcome.NotAGitRepo)
        {
            ctx.Get<IMessageBus>()?.Broadcast(new ShowOperationErrorMessage(
                s.ReposErrorNotAGitRepoTitle,
                s.ReposErrorNotAGitRepoMessage(path)));
        }
    }

    private static void ShowCloneDialog(Context ctx)
        => ctx.Get<IMessageBus>()?.Broadcast(
            new ShowDialogMessage(onClose => new CloneRepoDialog { OnClose = onClose }));

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
