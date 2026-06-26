using GitBench.Features.Operations;
using GitBench.Features.Toolbar;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

/// <summary>
/// Working area for the selected repo: the branches rail beside the operation banner and
/// actions toolbar stacked above the main content (history or local changes).
/// </summary>
internal sealed record WorkspaceWidget : Widget
{
    protected override IWidget Build(Context ctx) => new BorderLayout
    {
        West = new BranchesSidebar(),
        Center = new BorderLayout
        {
            North = new Column
            {
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = [new OperationBannerWidget(), new ActionsToolbar()],
            },
            Center = new MainContent(),
            South = new OperationPanelWidget(),
        },
    };
}
