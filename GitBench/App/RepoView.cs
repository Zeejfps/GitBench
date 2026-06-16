using GitBench.Features.Branches;
using GitBench.Features.Submodules;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

/// <summary>
/// Everything right of the repo rail for the selected repo: the detached-HEAD and
/// submodule-status banners stacked above the workspace. Both banners self-hide (collapsing
/// their slot) when not relevant, so they're visible on any tab without reserving space.
/// </summary>
internal sealed record RepoView : Widget
{
    protected override IWidget Build(Context ctx) => new Column
    {
        CrossAxis = CrossAxisAlignment.Stretch,
        Children =
        [
            new DetachedHeadBannerView(),
            new SubmoduleStatusBannerView(),
            new Grow { Child = new WorkspaceView() },
        ],
    };
}
