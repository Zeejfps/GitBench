using GitBench.Features.Repos;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.App;

/// <summary>Left rail listing repositories, with a draggable splitter whose width persists.</summary>
internal sealed record RepoBarSidebar : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var preferences = ctx.Require<PreferencesService>();
        return new ResizableSidebar
        {
            Content = new RepoBar(),
            InitialWidth = preferences.Current.RepoBarWidth,
            MinResizeWidth = 220f,
            OnWidthChanged = preferences.SetRepoBarWidth,
        };
    }
}
