using GitBench.Controls;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

public sealed record RepoRow : Widget
{
    public required Repo Repo { get; init; }

    protected override View CreateView(Context ctx)
    {
        var repo = Repo;
        var registry = ctx.Require<IRepoRegistry>();
        var status = ctx.Require<IRepoStatusStore>();
        var theme = ctx.Theme();

        var isHovered = new State<bool>(false);

        // Chevron slot is always present so primaries with and without worktrees share
        // alignment. The slot becomes interactive (and visible) only when children exist.
        var chevronSlot = new WorktreeChevron { Repo = repo }.BuildView(ctx);

        var icon = new TextView(ctx.Canvas)
        {
            Text = LucideIcons.FolderGit2,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            Width = RepoBar.RowIconWidth,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        RowChrome.BindRowText(theme, icon, registry, repo);

        var label = new TextView(ctx.Canvas)
        {
            Text = repo.DisplayName,
            HorizontalTextAlignment = TextAlignment.Start,
            VerticalTextAlignment = TextAlignment.Center,
            TextOverflow = TextOverflow.Ellipsis,
        };
        RowChrome.BindRowText(theme, label, registry, repo);

        var statusBadge = RowChrome.CreateBadge(theme, status, repo.Id);

        var background = new RectView
        {
            BorderRadius = BorderRadiusStyle.All(4),
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle { Left = RepoBar.RowPaddingLeft, Right = 12 },
                    Children =
                    {
                        new FlexRowView
                        {
                            Gap = RepoBar.RowIconGap,
                            CrossAxisAlignment = CrossAxisAlignment.Center,
                            Children =
                            {
                                chevronSlot,
                                icon,
                                new FlexItem { Grow = 1, Child = label },
                                statusBadge,
                            }
                        }
                    }
                }
            }
        };
        RowChrome.BindRowBackground(theme, background, isHovered, registry, repo.Id);

        var root = new ContainerView { Height = 28 };
        root.Children.Add(background);

        root.UseController(ctx.Require<InputSystem>(), () => new RepoRowController(
            root, ctx,
            repo,
            registry,
            h => isHovered.Value = h,
            _ => BuildMenuItems(repo, registry, ctx)));
        return root;
    }

    private static IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems(Repo repo, IRepoRegistry registry, Context context)
    {
        var sourceGroup = registry.FindGroupContaining(repo.Id);
        var items = new List<RepoBarContextMenu.Item>();

        var bus = context.Get<IMessageBus>();
        if (bus is not null)
        {
            items.Add(new RepoBarContextMenu.Item(
                "New worktree…",
                () => bus.Broadcast(new ShowDialogMessage(onClose => new CreateWorktreeDialog { Primary = repo, OnClose = onClose })),
                LucideIcons.Branch));

            var git = context.Get<IGitService>();
            if (git is not null)
            {
                items.Add(new RepoBarContextMenu.Item(
                    "Prune worktrees",
                    () =>
                    {
                        Task.Run(() => git.PruneWorktrees(repo));
                        bus.Broadcast(new WorktreesChangedMessage(repo.Id));
                    },
                    LucideIcons.Trash));
            }

            items.Add(new RepoBarContextMenu.Item(
                "Add submodule…",
                () => bus.Broadcast(new ShowDialogMessage(onClose => new AddSubmoduleDialog { Primary = repo, OnClose = onClose })),
                LucideIcons.Package));

            items.Add(new RepoBarContextMenu.Item(
                "Update all submodules…",
                () => bus.Broadcast(new ShowDialogMessage(onClose => new UpdateSubmodulesDialog { Primary = repo, Target = null, OnClose = onClose })),
                LucideIcons.Pull));
        }

        foreach (var group in registry.Groups)
        {
            if (sourceGroup != null && group.Id == sourceGroup.Id) continue;
            var captured = group;
            items.Add(new RepoBarContextMenu.Item(
                $"Move to: {captured.Name}",
                () => registry.MoveRepo(repo.Id, captured.Id, captured.RepoIds.Count),
                LucideIcons.FolderInput));
        }

        items.Add(new RepoBarContextMenu.Item(
            "Remove repo",
            () => registry.RemoveRepo(repo.Id),
            LucideIcons.Trash));

        items.Add(new RepoBarContextMenu.Item(
            "New group",
            () =>
            {
                var id = registry.CreateGroup("New Group");
                registry.BeginRenameGroup(id);
            },
            LucideIcons.FolderPlus));

        return items;
    }
}
