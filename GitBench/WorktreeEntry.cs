using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitBench;

// Worktree analogue of RepoEntry / SubmoduleEntry: a worktree row plus its collapsible
// submodule children (a worktree shares the primary's .gitmodules).
public sealed class WorktreeEntry : MultiChildView
{
    public WorktreeEntry(Repo worktree, IRepoRegistry registry, IRepoBadgeStore badges, int depth)
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
                if (!registry.IsWorktreeExpanded(worktree.Id))
                    return System.Linq.Enumerable.Empty<View>();
                return RepoTreeChildren.Build(worktree.Id, registry, badges, depth + 1);
            },
            v => v);

        AddChildToSelf(new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new WorktreeRow(worktree, registry, badges, depth),
                children,
            },
        });
    }
}
