using GitBench.Features.Repos;
using GitBench.Features.Worktrees;
using GitBench.Git;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Submodules;

// Composite that renders a single submodule row with its own (collapsible) nested children
// stacked below — the submodule analogue of RepoEntry. This is what lets the RepoBar show
// submodules-of-submodules: each SubmoduleEntry recurses via RepoTreeChildren, so the tree
// extends as deep as the discovery walk found. Folds independently of its parent (its own
// chevron drives IsWorktreeExpanded for its id).
public sealed record SubmoduleEntry : Widget
{
    public required Repo Submodule { get; init; }
    public required int Depth { get; init; }

    protected override View CreateView(Context ctx)
    {
        var submodule = Submodule;
        var depth = Depth;
        var registry = ctx.Require<IRepoRegistry>();

        var children = new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };

        children.Children.BindChildren(
            () =>
            {
                _ = registry.WorktreesChanged.Value;
                if (!registry.IsWorktreeExpanded(submodule.Id))
                    return System.Linq.Enumerable.Empty<View>();
                return RepoTreeChildren.Build(ctx, submodule.Id, registry, depth + 1);
            },
            v => v);

        var root = new ContainerView();
        root.Children.Add(new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new SubmoduleRow { Repo = submodule, Depth = depth }.BuildView(ctx),
                children,
            },
        });
        return root;
    }
}

// Shared factory for the child rows under any RepoBar parent (primary, worktree, or
// submodule). Worktrees first, then submodules — both recursive composites that carry their
// own nested children, same order/indent at every level.
internal static class RepoTreeChildren
{
    public static IReadOnlyList<View> Build(Context ctx, System.Guid parentId, IRepoRegistry registry, int depth)
    {
        var views = new List<View>();
        foreach (var r in registry.Repos)
        {
            if (r.ParentRepoId == parentId && r.IsWorktree)
                views.Add(new WorktreeEntry { Worktree = r, Depth = depth }.BuildView(ctx));
        }
        foreach (var r in registry.Repos)
        {
            if (r.ParentRepoId == parentId && r.IsSubmodule)
                views.Add(new SubmoduleEntry { Submodule = r, Depth = depth }.BuildView(ctx));
        }
        return views;
    }
}
