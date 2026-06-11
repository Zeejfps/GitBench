using GitBench.Features.Submodules;
using GitBench.Git;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

// Composite that renders a single primary repo with its (collapsible) child rows
// (worktrees + submodules) stacked below. Used as the row factory of GroupSection so
// the existing group-level list binding doesn't need to know about nesting.
//
// Worktrees come first, then submodules — same indent for both. Kinds are distinguished
// by icon shape AND a tinted accent color (Branch + blue for worktree, FolderGit2 +
// purple for submodule); no separator rows.
public sealed record RepoEntry : Widget
{
    public required Repo Primary { get; init; }

    protected override View CreateView(Context ctx)
    {
        var primary = Primary;
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
                if (!registry.IsWorktreeExpanded(primary.Id))
                    return System.Linq.Enumerable.Empty<View>();
                // Direct children sit at depth 1; SubmoduleEntry recurses for deeper nesting.
                return RepoTreeChildren.Build(ctx, primary.Id, registry, depth: 1);
            },
            v => v);

        var root = new ContainerView();
        root.Children.Add(new FlexColumnView
        {
            Gap = 2,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new RepoRow { Repo = primary }.BuildView(ctx),
                children,
            }
        });
        return root;
    }
}
