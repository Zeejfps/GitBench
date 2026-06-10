using GitBench.Features.Repos;
using GitBench.Features.Worktrees;
using GitBench.Git;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitBench.Features.Submodules;

// Composite that renders a single submodule row with its own (collapsible) nested children
// stacked below — the submodule analogue of RepoEntry. This is what lets the RepoBar show
// submodules-of-submodules: each SubmoduleEntry recurses via RepoTreeChildren, so the tree
// extends as deep as the discovery walk found. Folds independently of its parent (its own
// chevron drives IsWorktreeExpanded for its id).
public sealed class SubmoduleEntry : MultiChildView
{
    public SubmoduleEntry(Repo submodule, IRepoRegistry registry, IRepoStatusStore status, int depth)
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
                if (!registry.IsWorktreeExpanded(submodule.Id))
                    return System.Linq.Enumerable.Empty<View>();
                return RepoTreeChildren.Build(submodule.Id, registry, status, depth + 1);
            },
            v => v);

        AddChildToSelf(new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new SubmoduleRow(submodule, registry, status, depth),
                children,
            },
        });
    }
}

// Shared factory for the child rows under any RepoBar parent (primary, worktree, or
// submodule). Worktrees first, then submodules — both recursive composites that carry their
// own nested children, same order/indent at every level.
internal static class RepoTreeChildren
{
    public static IReadOnlyList<View> Build(System.Guid parentId, IRepoRegistry registry, IRepoStatusStore status, int depth)
    {
        var views = new List<View>();
        foreach (var r in registry.Repos)
        {
            if (r.ParentRepoId == parentId && r.IsWorktree)
                views.Add(new WorktreeEntry(r, registry, status, depth));
        }
        foreach (var r in registry.Repos)
        {
            if (r.ParentRepoId == parentId && r.IsSubmodule)
                views.Add(new SubmoduleEntry(r, registry, status, depth));
        }
        return views;
    }
}
