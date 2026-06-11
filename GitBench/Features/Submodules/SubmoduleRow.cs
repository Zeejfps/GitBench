using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Theming;
using ZGF.Gui;

namespace GitBench.Features.Submodules;

// Renders a single submodule row nested under its parent in the RepoBar. Same shape
// as WorktreeRow (deep indent + small icon) but uses the Package icon + purple tint
// to signal "this is an embedded external repository pinned to a specific commit,"
// not a sibling checkout of the parent.
public sealed record SubmoduleRow : NestedRepoRow
{
    protected override string IconGlyph => LucideIcons.Package;

    // Package icon + purple tint — submodules are mentally "external packages
    // embedded at a pinned commit," visually distinct from the FolderGit2 used for
    // primary repos. Tint matches the StatusSubmodule badge used by pointer-change
    // rows in CommitDetails so the visual language stays consistent across the app.
    protected override uint SelectAccent(RepoBarRowStyles s) => s.IconAccentSubmodule;

    // A submodule that's been added but not initialized has no .git directory of its
    // own and would render an empty BranchesView/HistoryView. Better to do nothing —
    // the user can right-click → Update… to initialize it.
    protected override Func<bool>? CanActivate => () => !Repo.IsMissing;

    protected override IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems(Context ctx)
        => BuildMenuItems(Repo, ctx.Require<IRepoRegistry>(), ctx);

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
                    () => bus.Broadcast(new ShowDialogMessage(onClose => new UpdateSubmodulesDialog { Primary = primary, Target = submodule, OnClose = onClose })),
                    LucideIcons.Pull));
                items.Add(new RepoBarContextMenu.Item(
                    "Deinit submodule…",
                    () => bus.Broadcast(new ShowDialogMessage(onClose => new DeinitSubmoduleDialog { Primary = primary, Submodule = submodule, OnClose = onClose })),
                    LucideIcons.Trash));
            }
        }

        return items;
    }
}
