using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Shell for the main content area: shows the view for the active <see cref="MainViewMode"/> —
/// commit history or working changes.
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
                _ => Empty.Widget,
            },
        };
    }
}
