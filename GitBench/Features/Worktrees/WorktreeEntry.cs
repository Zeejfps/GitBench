using GitBench.Features.Repos;
using GitBench.Features.Submodules;
using GitBench.Git;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Worktrees;

// Worktree analogue of RepoEntry / SubmoduleEntry: a worktree row plus its collapsible
// submodule children (a worktree shares the primary's .gitmodules).
public sealed record WorktreeEntry : Widget
{
    public required Repo Worktree { get; init; }
    public required int Depth { get; init; }

    protected override View CreateView(Context ctx)
    {
        var worktree = Worktree;
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
                if (!registry.IsWorktreeExpanded(worktree.Id))
                    return System.Linq.Enumerable.Empty<View>();
                return RepoTreeChildren.Build(ctx, worktree.Id, registry, depth + 1);
            },
            v => v);

        var root = new ContainerView();
        root.Children.Add(new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new WorktreeRow { Repo = worktree, Depth = depth }.BuildView(ctx),
                children,
            },
        });
        return root;
    }
}
