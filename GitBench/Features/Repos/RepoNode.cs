using GitBench.Controls;
using GitBench.Git;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

// One RepoBar row plus its collapsible subtree (worktrees + submodules), recursively. Resolves its
// node view model from the surrounding Each scope; the child rows mirror the view model's child list,
// which is empty while the row is collapsed.
internal sealed record RepoNode : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<RepoNodeViewModel>();
        return new Column
        {
            Gap = 2,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                vm.Kind == RepoKind.Primary
                    ? new PrimaryRepoRow().WithController<RepoRowController>()
                    : new NavigableRepoRow().WithController<NavigableRowController>(),
                new Each<RepoNodeViewModel>
                {
                    Items = vm.Children,
                    Template = new RepoNode(),
                    Gap = 2,
                    CrossAxis = CrossAxisAlignment.Stretch,
                },
            ],
        };
    }
}
