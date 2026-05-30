using ZGF.Gui;

namespace GitGui;

// Renders a single worktree row, nested under its primary in the RepoBar. Visually
// distinguished from primary rows by deeper indent and the Branch icon.
public sealed class WorktreeRow : NestedRepoRow
{
    public WorktreeRow(Repo worktree, IRepoRegistry registry)
        : base(
            worktree,
            registry,
            LucideIcons.Branch,
            // Tinted by kind so the sidebar tells worktree apart from primary / submodule
            // without leaning on a header row. Missing rows mute the accent to match the label.
            s => s.IconAccentWorktree,
            ctx => BuildMenuItems(worktree, registry, ctx))
    {
    }

    private static IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems(
        Repo worktree, IRepoRegistry registry, Context context)
    {
        var items = new List<RepoBarContextMenu.Item>();

        items.Add(new RepoBarContextMenu.Item(
            "Switch to worktree",
            () => registry.SetActive(worktree.Id),
            LucideIcons.Branch));

        var shell = context.Get<IPlatformShell>();
        if (shell is not null)
        {
            items.Add(new RepoBarContextMenu.Item(
                "Open folder",
                () => shell.OpenFolder(worktree.Path),
                LucideIcons.FolderOpen));
        }

        var bus = context.Get<IMessageBus>();
        if (bus is not null && worktree.ParentRepoId is { } parentId)
        {
            var primary = FindRepo(registry, parentId);
            if (primary is not null)
            {
                items.Add(new RepoBarContextMenu.Item(
                    "Remove worktree…",
                    () => bus.Broadcast(new ShowDialogMessage(onClose => new RemoveWorktreeDialog(primary, worktree, onClose))),
                    LucideIcons.Trash));
            }
        }

        return items;
    }
}
