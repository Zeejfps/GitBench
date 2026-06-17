using GitBench.Features.Repos;
using GitBench.Features.Worktrees;
using GitBench.Git;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Submodules;

// Composite that renders a single submodule row with its own (collapsible) nested children
// stacked below — the submodule analogue of RepoEntry. This is what lets the RepoBar show
// submodules-of-submodules: each SubmoduleEntry recurses via RepoTreeChildren, so the tree
// extends as deep as the discovery walk found. Folds independently of its parent — its own
// chevron toggles the expand state keyed to its id.
public sealed record SubmoduleEntry : Widget
{
    public required Repo Submodule { get; init; }
    public required int Depth { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var submodule = Submodule;
        var registry = ctx.Require<IRepoRegistry>();
        var expanded = registry.WatchWorktreeExpanded(submodule.Id);
        var childDepth = Depth + 1;

        return new Column
        {
            Gap = 2,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new SubmoduleRow { Repo = submodule, Depth = Depth },
                new Column<Repo>
                {
                    Gap = 2,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Items = Prop.Bind(() => expanded.Value
                        ? RepoTreeChildren.ChildRepos(submodule.Id, registry)
                        : Array.Empty<Repo>()),
                    Template = repo => RepoTreeChildren.Entry(repo, childDepth),
                },
            ],
        };
    }
}

// Shared factory for the child rows under any RepoBar parent (primary, worktree, or submodule).
internal static class RepoTreeChildren
{
    // Worktrees first, then submodules — both recursive composites that carry their own nested
    // children, same order/indent at every level.
    public static IReadOnlyList<Repo> ChildRepos(Guid parentId, IRepoRegistry registry)
    {
        var repos = new List<Repo>();
        foreach (var r in registry.Repos)
            if (r.ParentRepoId == parentId && r.IsWorktree) repos.Add(r);
        foreach (var r in registry.Repos)
            if (r.ParentRepoId == parentId && r.IsSubmodule) repos.Add(r);
        return repos;
    }

    public static IWidget Entry(Repo repo, int depth) => repo.IsWorktree
        ? new WorktreeEntry { Worktree = repo, Depth = depth }
        : new SubmoduleEntry { Submodule = repo, Depth = depth };

    public static IReadOnlyList<View> Build(Context ctx, Guid parentId, IRepoRegistry registry, int depth)
    {
        var views = new List<View>();
        foreach (var r in ChildRepos(parentId, registry))
            views.Add(Entry(r, depth).BuildView(ctx));
        return views;
    }
}
