using GitBench.Features.StatusBar;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

/// <summary>
/// The workspace screen shown while at least one repository is open: the repo sidebar, the active
/// repo's view, and the status bar. Swapped for <see cref="WelcomeScreenWidget"/> when no repos are open.
/// </summary>
internal sealed record MainScreenWidget : Widget
{
    protected override IWidget Build(Context ctx) => new BorderLayout
    {
        West = new RepoBarSidebar(),
        Center = new RepoView(),
        South = new StatusBarView(),
    };
}
