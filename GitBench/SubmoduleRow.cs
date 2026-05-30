using ZGF.Gui;

namespace GitGui;

// Renders a single submodule row nested under its parent in the RepoBar. Same shape
// as WorktreeRow (deep indent + small icon) but uses the Package icon + purple tint
// to signal "this is an embedded external repository pinned to a specific commit,"
// not a sibling checkout of the parent.
public sealed class SubmoduleRow : NestedRepoRow
{
    public SubmoduleRow(Repo submodule, IRepoRegistry registry)
        : base(
            submodule,
            registry,
            LucideIcons.Package,
            // Package icon + purple tint — submodules are mentally "external packages
            // embedded at a pinned commit," visually distinct from the FolderGit2 used for
            // primary repos. Tint matches the StatusSubmodule badge used by pointer-change
            // rows in CommitDetails so the visual language stays consistent across the app.
            s => s.IconAccentSubmodule,
            ctx => BuildMenuItems(submodule, registry, ctx),
            // A submodule that's been added but not initialized has no .git directory of its
            // own and would render an empty BranchesView/HistoryView. Better to do nothing —
            // the user can right-click → Update… to initialize it.
            canActivate: () => !submodule.IsMissing)
    {
    }

    private static IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems(
        Repo submodule, IRepoRegistry registry, Context context)
    {
        var items = new List<RepoBarContextMenu.Item>();

        if (!submodule.IsMissing)
        {
            items.Add(new RepoBarContextMenu.Item(
                "Switch to submodule",
                () => registry.SetActive(submodule.Id),
                LucideIcons.Package));
        }

        var shell = context.Get<IPlatformShell>();
        if (shell is not null)
        {
            items.Add(new RepoBarContextMenu.Item(
                "Open folder",
                () => shell.OpenFolder(submodule.Path),
                LucideIcons.FolderOpen));
        }

        var bus = context.Get<IMessageBus>();
        if (bus is not null && submodule.ParentRepoId is { } parentId)
        {
            var primary = FindRepo(registry, parentId);
            if (primary is not null)
            {
                items.Add(new RepoBarContextMenu.Item(
                    "Update submodule…",
                    () => bus.Broadcast(new ShowDialogMessage(onClose => new UpdateSubmodulesDialog(primary, submodule, onClose))),
                    LucideIcons.Pull));
                items.Add(new RepoBarContextMenu.Item(
                    "Deinit submodule…",
                    () => bus.Broadcast(new ShowDialogMessage(onClose => new DeinitSubmoduleDialog(primary, submodule, onClose))),
                    LucideIcons.Trash));
            }
        }

        return items;
    }
}
