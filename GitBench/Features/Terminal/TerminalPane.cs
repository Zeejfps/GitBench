using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Pty;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// The terminal pane: a shell running in the active repository, drawn as a cell grid.
/// </summary>
/// <remarks>
/// The mode switcher keeps this pane alive once it has been built, so the shell it starts survives
/// switching to History and back — and, by the same token, nothing here runs until the user picks
/// the Terminal mode for the first time.
/// </remarks>
internal sealed record TerminalPane : Widget
{
    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Require<IThemeService<ThemeStyles>>();
        var loc = ctx.Require<ILocalizationService>();
        var registry = ctx.Require<IRepoRegistry>();
        var sessions = ctx.Require<IPtySessionFactory>();
        var engines = ctx.Require<ITerminalEngineFactory>();
        var dispatcher = ctx.Require<IUiDispatcher>();

        var view = new TerminalGridView(theme)
        {
            StartingMessage = loc.Strings.Value.TerminalStarting,
        };

        view.Bind(loc.Strings, s => view.StartingMessage = s.TerminalStarting);

        if (registry.Active.Value is not { } repo)
        {
            view.SetRenderState(new TerminalRenderState.Failed(loc.Strings.Value.TerminalNoRepo));
            return view;
        }

        view.UseViewModel(
            () => new TerminalViewModel(sessions, engines, dispatcher, repo.Path),
            vm =>
            {
                view.OnViewportChanged = vm.ReportViewport;
                vm.Updated += view.Repaint;
                view.Bind(vm.RenderState, view.SetRenderState);
            });

        return view;
    }
}
