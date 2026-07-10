using GitBench.Features.Repos;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.App;

/// <summary>
/// Left rail listing repositories: the full bar with a draggable splitter whose width persists,
/// or the compact icon rail while collapsed.
/// </summary>
internal sealed record RepoBarSidebar : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var preferences = ctx.Require<PreferencesService>();
        var collapse = ctx.Require<RepoBarCollapseState>();
        return new Switch<bool>
        {
            Value = collapse.IsCollapsed,
            Case = collapsed => collapsed
                ? new RepoRail()
                : new ResizableSidebar
                {
                    Content = new RepoBar(),
                    InitialWidth = preferences.Current.RepoBarWidth,
                    MinResizeWidth = 220f,
                    OnWidthChanged = preferences.SetRepoBarWidth,
                },
        };
    }
}
