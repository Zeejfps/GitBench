using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Pty;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
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
    /// <summary>
    /// Path to a recorded session to replay instead of spawning a shell — a probe-harness
    /// <c>.bin</c>, with its <c>.inventory.txt</c> beside it. A launch-time development aid: it puts
    /// a known screen in front of the renderer, so what is drawn can be read against the corpus
    /// suite's golden for the same recording without a shell, a spawn, or a program that behaves the
    /// same way twice.
    /// </summary>
    public const string ReplayEnvVar = "DIFFDINO_TERMINAL_REPLAY";

    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Require<IThemeService<ThemeStyles>>();
        var loc = ctx.Require<ILocalizationService>();
        var dispatcher = ctx.Require<IUiDispatcher>();
        var input = ctx.Require<InputSystem>();

        var view = new TerminalGridView(theme)
        {
            StartingMessage = loc.Strings.Value.TerminalStarting,
        };

        view.Bind(loc.Strings, s => view.StartingMessage = s.TerminalStarting);

        if (!TryResolveLaunch(ctx, loc, out var launch, out var problem))
        {
            view.SetRenderState(new TerminalRenderState.Failed(problem));
            return view;
        }

        view.UseViewModel(
            () => new TerminalViewModel(launch, dispatcher),
            vm =>
            {
                view.OnViewportChanged = vm.ReportViewport;
                vm.Updated += view.Repaint;
                view.Bind(vm.RenderState, view.SetRenderState);
                view.UseController(input, () => new TerminalInputController(view, input, vm));
            });

        return view;
    }

    /// <remarks>
    /// The recording is loaded here rather than inside the launch so that a missing or unreadable
    /// one becomes a message in the pane, not an exception thrown out of the first draw.
    /// </remarks>
    static bool TryResolveLaunch(
        Context ctx,
        ILocalizationService loc,
        out ITerminalLaunch launch,
        out string problem)
    {
        var engines = ctx.Require<ITerminalEngineFactory>();
        problem = string.Empty;
        launch = null!;

        var replayPath = Environment.GetEnvironmentVariable(ReplayEnvVar);
        if (!string.IsNullOrWhiteSpace(replayPath))
        {
            try
            {
                launch = new ReplayLaunch(TerminalRecording.Load(replayPath), engines);
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return false;
            }
        }

        if (ctx.Require<IRepoRegistry>().Active.Value is not { } repo)
        {
            problem = loc.Strings.Value.TerminalNoRepo;
            return false;
        }

        launch = new ShellLaunch(repo.Path, ctx.Require<IPtySessionFactory>(), engines);
        return true;
    }
}
