using GitBench.Features.Commits;
using GitBench.Features.FileBrowser;
using GitBench.Features.LocalChanges;
using GitBench.Features.Terminal;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Shell for the main content area: shows the view for the active <see cref="MainViewMode"/> —
/// commit history, working changes, the terminal, or the files on disk.
/// </summary>
internal sealed record MainContent : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var mode = ctx.Require<State<MainViewMode>>();
        return new Switch<MainViewMode>
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
        };
    }
}
