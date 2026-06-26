using GitBench.Features.Toolbar;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.App;

internal sealed record WorkspaceWidget : Widget
{
    protected override IWidget Build(Context ctx) => new BorderLayout
    {
        West = new BranchesSidebar(),
        Center = new BorderLayout
        {
            North = new ActionsToolbar(),
            Center = new MainContent(),
        },
    };
}
