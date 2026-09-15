using GitBench.Features.Branches;
using GitBench.Features.StatusBar;
using GitBench.Features.Submodules;
using GitBench.Features.Toolbar;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

/// <summary>
/// The workspace screen shown while at least one repository is open: the repo sidebar, the active
/// repo's banners, branch rail, toolbar and content panel, and the status bar. Swapped for
/// <see cref="WelcomeScreenWidget"/> when no repos are open.
/// </summary>
internal sealed record MainScreenWidget : Widget
{
    protected override IWidget Build(Context ctx) => new BorderLayout
    {
        West = new RepoBarSidebar(),
        Center = new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new DetachedHeadBanner(),
                new SubmoduleStatusBanner(),
                new Grow
                {
                    Child = new BorderLayout
                    {
                        West = new BranchesSidebar(),
                        Center = new BorderLayout
                        {
                            North = new ActionsToolbar(),
                            Center = new MainContent(),
                        },
                    },
                },
            ],
        },
        South = new StatusBarView(),
    };
}
