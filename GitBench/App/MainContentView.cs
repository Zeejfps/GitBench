using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Shell for the main content area. Builds both mode-specific views (history and local changes)
/// up front and toggles their visibility based on the observed <see cref="MainViewMode"/>. The
/// inactive view stays mounted so its view model keeps listening to bus events and stays
/// continuously up to date — switching modes is then just a visibility flip, with no reload
/// flash and no "Loading…" placeholder. Both fill the area (a <see cref="Stack"/> gives each
/// child the full bounds); only one is visible at a time.
/// </summary>
internal sealed record MainContentView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var mode = ctx.Require<State<MainViewMode>>();
        return new Stack
        {
            Children =
            [
                new CommitHistory { BindVisible = () => mode.Value == MainViewMode.History },
                new LocalChangesPane { BindVisible = () => mode.Value == MainViewMode.LocalChanges },
            ],
        };
    }
}
