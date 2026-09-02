using GitBench.Controls;
using GitBench.Platform;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// The Terminal mode: the active repository's terminals, as a strip of tabs over one cell grid.
/// </summary>
/// <remarks>
/// One pane, and several terminals per repository behind it. The pane follows
/// <see cref="ITerminalSessionStore.Tabs"/> rather than reading the registry itself, so switching
/// repositories swaps the whole strip while the shells it leaves keep running — the terminals are
/// the store's, and the pane is only ever looking at one of them.
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

    protected override IWidget Build(Context ctx)
    {
        if (Environment.GetEnvironmentVariable(ReplayEnvVar) is { Length: > 0 } replayPath)
            return new TerminalReplayScreen { RecordingPath = replayPath };

        return new Switch<TerminalTabs?>
        {
            Value = ctx.Require<ITerminalSessionStore>().Tabs,
            Case = tabs => tabs is null
                ? new TerminalNotice { Message = L.T(s => s.TerminalNoRepo) }
                : new TerminalTabsPane { Tabs = tabs },
        };
    }
}

/// <summary>
/// One repository's terminals: the strip naming them, and the grid of whichever is active.
/// </summary>
/// <remarks>
/// The strip appears with the first shell. Until then the pane is one screen offering to start one,
/// and a tab strip over it would be naming a terminal that has not run anything.
/// </remarks>
internal sealed record TerminalTabsPane : Widget
{
    public required TerminalTabs Tabs { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var tabs = Tabs;

        return new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new TerminalTabStrip
                {
                    Tabs = tabs,
                    Visible = Prop.Bind(() => tabs.AnyStarted),
                },
                new Grow
                {
                    Child = new Switch<TerminalInstance>
                    {
                        Value = tabs.Active,
                        Case = instance => new TerminalScreen { Instance = instance },
                    },
                },
            ],
        };
    }
}

/// <summary>
/// One terminal on screen: its grid, its keyboard, and the offer to start a shell when it has none.
/// </summary>
/// <remarks>
/// <para>
/// The terminal outlives this view, so everything wired here is unwired on unmount. That is not the
/// usual bookkeeping — a view model dying with its view needs none of it — and it is why the repaint
/// hookup is a scoped behavior rather than a bare <c>+=</c>: a pane rebuilt on every repository
/// switch would otherwise leave the instance holding a dead grid view per switch.
/// </para>
/// <para>
/// The grid stays mounted in every state, including the states where a gate is covering it. It is
/// the only thing that can measure a cell against the canvas, so it is the only thing that can say
/// how big a shell should start — and the gate needs that answer to already exist by the time it is
/// clicked.
/// </para>
/// </remarks>
internal sealed record TerminalScreen : Widget
{
    /// <summary>The grid's id, so a test — or the live debug server — can address the screen itself
    /// rather than the layers stacked over it.</summary>
    public const string GridId = "terminal-grid";

    public required TerminalInstance Instance { get; init; }

    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Require<IThemeService<ThemeStyles>>();
        var loc = ctx.Require<ILocalizationService>();
        var input = ctx.Require<InputSystem>();
        var instance = Instance;

        var grid = new TerminalGridView(theme)
        {
            Id = GridId,
            StartingMessage = loc.Strings.Value.TerminalStarting,
            OnViewportChanged = instance.ReportViewport,
        };

        grid.Bind(loc.Strings, s => grid.StartingMessage = s.TerminalStarting);
        grid.Bind(instance.Render, grid.SetRenderState);
        grid.Use(() => new TerminalRepaintLink(instance, grid));
        grid.UseController(input, () => new TerminalInputController(
            grid, input, instance, grid, ctx.Get<IClipboard>(), ctx.Get<IPlatformShell>(), ctx, loc));
        grid.Use(() => new TerminalKeyboardHandover(instance, grid, input));

        return new Stack
        {
            Children =
            [
                new Raw { View = grid },
                new TerminalStartGate
                {
                    Instance = instance,
                    OnStart = () =>
                    {
                        instance.Start();

                        // The click that started the shell is also the one that should have left the
                        // keyboard in it. The terminal's own controller does not take focus while it
                        // has no shell to type into, so the gate hands it over on the way out.
                        if (input.GetController(grid) is { } controller) input.StealFocus(controller);
                    },
                },
            ],
        }.BuildView(ctx);
    }
}

/// <summary>
/// The replay dev aid's terminal: one instance, owned by this view rather than by the store, since
/// a recording belongs to no repository.
/// </summary>
/// <remarks>
/// Started as soon as it is built. A recording is not something a user asked for and has nothing to
/// decide about, so it does not get the gate; a recording that cannot be read fails the start and
/// says so in the pane, which is where a launch-time aid's failure belongs.
/// </remarks>
internal sealed record TerminalReplayScreen : Widget
{
    public required string RecordingPath { get; init; }

    protected override View CreateView(Context ctx)
    {
        TerminalRecording recording;
        try
        {
            // Read here rather than inside the launch so that a missing or unreadable recording
            // becomes a message in the pane, not an exception thrown out of the first draw.
            recording = TerminalRecording.Load(RecordingPath);
        }
        catch (Exception ex)
        {
            return new TerminalNotice { Message = ex.Message }.BuildView(ctx);
        }

        var instance = new TerminalInstance(
            new ReplayLaunch(
                recording,
                ctx.Require<ITerminalEngineFactory>(),
                Path.GetFileNameWithoutExtension(RecordingPath)),
            ctx.Require<IUiDispatcher>());
        instance.Start();

        var view = new TerminalScreen { Instance = instance }.BuildView(ctx);
        view.Use(() => instance);
        return view;
    }
}

/// <summary>A line of text where a terminal would be, on the terminal's own background.</summary>
internal sealed record TerminalNotice : Widget
{
    public Prop<string?> Message { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.Terminal.DefaultBackground),
        Children =
        [
            new Center
            {
                Child = new Text
                {
                    Value = Message,
                    Color = Theme.Color(s => s.Palette.TextSecondary),
                },
            },
        ],
    };
}

/// <summary>
/// The offer to start a shell, over the screen of the one that finished.
/// </summary>
/// <remarks>
/// Shown for every state that has no shell running, which is what makes "start" and "start again"
/// one control rather than two. Nothing here is hit-testable except the button, so the wheel over an
/// exited screen still reaches the grid underneath it and scrolls its history.
/// </remarks>
internal sealed record TerminalStartGate : Widget
{
    /// <summary>The button's id, so a test can press the thing a user presses.</summary>
    public const string StartButtonId = "terminal-start-session";

    public required TerminalInstance Instance { get; init; }
    public required Action OnStart { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var instance = Instance;
        var loc = ctx.Require<ILocalizationService>();

        return new Center
        {
            Visible = Prop.Bind(() => instance.Render.Value is not
                (TerminalRenderState.Running or TerminalRenderState.Starting)),
            Child = new Column
            {
                Gap = Spacing.Md,
                CrossAxis = CrossAxisAlignment.Center,
                Children =
                [
                    new Text
                    {
                        Value = Prop.Bind(() => Reason(loc.Strings.Value, instance.Render.Value)),
                        Color = Theme.Color(s => s.Palette.TextSecondary),
                        HAlign = TextAlignment.Center,
                    },
                    new ButtonWidget
                    {
                        Id = StartButtonId,
                        Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                        Command = new Command(OnStart),
                        Children =
                        [
                            new ButtonIcon { Value = LucideIcons.SquareTerminal },
                            new ButtonLabel
                            {
                                Value = Prop.Bind(() => Label(loc.Strings.Value, instance.Render.Value)),
                            },
                        ],
                    }.WithController<KbmController>(),
                ],
            },
        };
    }

    /// <summary>Why there is no shell — nothing at all for a terminal that has not had one yet.</summary>
    static string? Reason(Strings strings, TerminalRenderState render) => render switch
    {
        TerminalRenderState.Exited => strings.TerminalSessionEnded,
        TerminalRenderState.Faulted faulted => faulted.Message,
        TerminalRenderState.Failed failed => failed.Message,
        _ => null,
    };

    static string Label(Strings strings, TerminalRenderState render) =>
        render is TerminalRenderState.Idle
            ? strings.TerminalStartSession
            : strings.TerminalRestartSession;
}

/// <summary>
/// Gives the keyboard to the terminal that has just been put on screen, once it has a shell.
/// </summary>
/// <remarks>
/// <para>
/// Bringing a terminal to the front is asking to type in it — switching tabs, opening one, or
/// switching to a repository whose shell is still running. Without this the keyboard stayed wherever
/// the last click left it, so every one of those had to be followed by a click on the grid.
/// </para>
/// <para>
/// It waits for the render state rather than taking the keyboard when the view mounts, because the
/// strip's <c>+</c> activates a tab before it starts it: that grid is mounted while its terminal is
/// still idle. An idle terminal is left alone for the same reason a click on one is — it is showing
/// the offer to start a shell, and the application's own chords have to survive over it.
/// </para>
/// <para>
/// Only while the pane is showing, and only once. The mode switcher keeps this view mounted behind
/// whichever mode is on screen, so a repository switched from another mode would otherwise hand the
/// keyboard to a terminal nobody can see; and a shell exiting long afterwards would pull it back
/// from wherever the reader had moved on to.
/// </para>
/// </remarks>
internal sealed class TerminalKeyboardHandover : IDisposable
{
    readonly TerminalGridView _grid;
    readonly InputSystem _input;
    readonly IDisposable _subscription;

    bool _handedOver;

    public TerminalKeyboardHandover(TerminalInstance instance, TerminalGridView grid, InputSystem input)
    {
        _grid = grid;
        _input = input;
        _subscription = instance.Render.Subscribe(OnRender);
    }

    void OnRender(TerminalRenderState render)
    {
        if (_handedOver || render is TerminalRenderState.Idle) return;
        if (_input.GetController(_grid) is not TerminalInputController controller) return;
        if (!controller.IsOnScreen) return;

        _handedOver = true;
        _input.StealFocus(controller);
    }

    public void Dispose() => _subscription.Dispose();
}

/// <summary>
/// Keeps the grid repainting while it is mounted, and stops when it is not.
/// </summary>
/// <remarks>
/// The instance outlives the view: an unsubscribe that only happened at the instance's own disposal
/// would leave one dead grid view attached per repository switch, each of them repainted for the
/// rest of the session.
/// </remarks>
internal sealed class TerminalRepaintLink : IDisposable
{
    readonly TerminalInstance _instance;
    readonly TerminalGridView _grid;

    public TerminalRepaintLink(TerminalInstance instance, TerminalGridView grid)
    {
        _instance = instance;
        _grid = grid;
        _instance.Updated += _grid.Repaint;
    }

    public void Dispose() => _instance.Updated -= _grid.Repaint;
}
