using GitBench.Features.Commits;
using GitBench.Features.FileBrowser;
using GitBench.Features.LocalChanges;
using GitBench.Features.Terminal;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// The content panel: the tab strip across the top, and under it whichever of the tabs is open —
/// working changes, commit history, the terminal, or a file.
/// </summary>
internal sealed record MainContent : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var mode = ctx.Require<State<MainViewMode>>();
        return new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new RepoContentTabs(),
                new Grow
                {
                    Child = new Switch<MainViewMode>
                    {
                        Value = mode,
                        KeepAlive = true,
                        Case = m => m switch
                        {
                            MainViewMode.History => new CommitHistory(),
                            MainViewMode.LocalChanges => new WorkingChanges(),
                            MainViewMode.Terminal => new TerminalPane(),
                            MainViewMode.Files => new FileBrowserPane(),
                            _ => Empty.Widget,
                        },
                    },
                },
            ],
        };
    }
}
