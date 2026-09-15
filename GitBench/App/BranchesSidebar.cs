using GitBench.Features.Branches;
using GitBench.Features.FileBrowser;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// The rail beside the content panel: the branch header over whichever of its two lists is showing,
/// with a width-persisting splitter.
/// </summary>
/// <remarks>
/// Branches and files share the rail rather than each having one, because they answer the same
/// question from two sides — where in the repository you are — and only one of them is ever the one
/// you are navigating by. The header stays put across the swap: which branch you are on is true of
/// both lists, so it is not part of what switches.
/// </remarks>
internal sealed record BranchesSidebar : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var preferences = ctx.Require<PreferencesService>();
        var pane = ctx.Require<State<SidebarPane>>();

        return new ResizableSidebar
        {
            Content = new Column
            {
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new BranchesHeader(),
                    new Grow
                    {
                        Child = new Switch<SidebarPane>
                        {
                            Value = pane,
                            // Both lists are scrolled to somewhere and expanded to something, and
                            // the toggle is a glance at the other one rather than a departure.
                            KeepAlive = true,
                            Case = p => p == SidebarPane.Files
                                ? new FileBrowserTreePane()
                                : new BranchesView(),
                        },
                    },
                ],
            },
            InitialWidth = preferences.Current.BranchesWidth,
            OnWidthChanged = w => preferences.Update(p => p with { BranchesWidth = w }),
        };
    }
}
