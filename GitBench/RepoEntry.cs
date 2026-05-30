using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

// Composite that renders a single primary repo with its (collapsible) child rows
// (worktrees + submodules) stacked below. Used as the row factory of GroupSection so
// the existing group-level list binding doesn't need to know about nesting.
//
// Worktrees come first, then submodules — same indent for both. Kinds are distinguished
// by icon shape AND a tinted accent color (Branch + blue for worktree, FolderGit2 +
// purple for submodule); no separator rows.
public sealed class RepoEntry : MultiChildView
{
    public RepoEntry(Repo primary, IRepoRegistry registry)
    {
        var children = new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };

        children.BindChildren(
            () =>
            {
                _ = registry.WorktreesChanged.Value;
                if (!registry.IsWorktreeExpanded(primary.Id))
                    return System.Linq.Enumerable.Empty<View>();

                var views = new List<View>();
                foreach (var r in registry.Repos)
                {
                    if (r.ParentRepoId == primary.Id && r.IsWorktree)
                        views.Add(new WorktreeRow(r, registry));
                }
                foreach (var r in registry.Repos)
                {
                    if (r.ParentRepoId == primary.Id && r.IsSubmodule)
                        views.Add(new SubmoduleRow(r, registry));
                }
                return views;
            },
            v => v);

        AddChildToSelf(new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new RepoRow(primary, registry),
                children,
            }
        });
    }
}
