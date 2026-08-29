using System.Collections.Concurrent;
using System.Text;
using GitBench.App;
using GitBench.Features.Assistant;
using GitBench.Features.Repos;
using GitBench.Features.Terminal;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

// ---------------------------------------------------------------------------------------------
// Seam suite for the terminal pane's keyboard: how it composes with what is already here. Not the
// encoding table (TerminalKeyEncoderTests) and not the per-key claim contract
// (TerminalInputControllerTests) - this file is about the joins.
//
//   * the collision with AppKeybindController, asserted on that controller's real effects
//   * the mode switcher's keep-alive swap: hidden but mounted, and what that costs the keyboard
//   * focus arbitration against other focus takers
//   * session lifecycle against input: before adopt, after the shell dies, after Dispose
//   * terminal modes read live from the engine rather than captured at construction
//   * what a focus-stealing controller on TerminalGridView costs the renderer's own tests
//
// Control bytes are always spelled as escapes and never written literally: a control character in
// a source literal is invisible in every diff and review that follows it.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// The pane itself, built the way the application builds it: the controller has to actually be
/// attached, or every other test in this suite passes and the Terminal tab does nothing.
/// </summary>
public class TerminalPaneWiringTests
{
    [Fact]
    public void BuildingThePane_AttachesAKeyboardToTheGrid()
    {
        // Deliberately never rendered. The view model starts its shell on the first viewport report,
        // which comes from a draw, so this builds the pane and inspects it without spawning anything.
        using var dir = new TempDir("gitbench-terminal-wiring-");
        using var harness = PaneHarness(dir.Path, out var view);

        var controller = harness.Input.GetController(view);

        Assert.IsType<TerminalInputController>(controller);
    }

    [Fact]
    public void ThePanesKeyboard_IsTheOneThatTakesFocusOnAClick()
    {
        using var dir = new TempDir("gitbench-terminal-wiring-");
        using var harness = PaneHarness(dir.Path, out var view);

        harness.Click(400f, 300f);

        Assert.Same(harness.Input.GetController(view), harness.Input.FocusedComponent);
    }

    static GuiTestHarness PaneHarness(string root, out View paneView)
    {
        var path = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        var statePath = Path.Combine(root, "state.json");
        var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        registry.Open(path);

        View? built = null;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                built = new TerminalPane().BuildView(ctx);
                return built;
            },
            width: 800,
            height: 600,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(
                    new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IUiDispatcher>(new QueuedDispatcher());
                ctx.AddService<ITerminalEngineFactory>(new XtermSharpEngineFactory());
                ctx.AddService<IPtySessionFactory>(new UnusedPtyFactory());
                ctx.AddService<IRepoRegistry>(registry);
            });

        paneView = built!;
        return harness;
    }

    /// <summary>A pseudo-terminal factory for a pane that is never drawn, and so never starts one.</summary>
    sealed class UnusedPtyFactory : IPtySessionFactory
    {
        public IPtySession Start(PtySessionOptions options) =>
            throw new InvalidOperationException("The pane started a shell without being drawn.");
    }
}

/// <summary>
/// The terminal's keyboard against the application's own, both registered in one input system:
/// which chords the terminal keeps, and - the part that matters - whether an application keybind it
/// declines actually still fires.
/// </summary>
/// <remarks>
/// The application side is the real AppKeybindController over real collaborators, so an assertion is
/// "the repo bar collapsed" rather than "somebody consumed the key". The two chords whose effect
/// runs through the assistant (Ctrl+K, Escape) are asserted on reach instead: those commands are
/// gated on a live assistant session, and standing one up would make the arrange block longer than
/// the suite.
/// </remarks>
public class TerminalKeybindCollisionTests : IDisposable
{
    private readonly CollidingApp _app = new();

    public void Dispose() => _app.Dispose();

    // ---- chords the terminal keeps ----

    [Fact]
    public void AFocusedTerminal_TakesCtrlB_AndTheRepoBarDoesNotCollapse()
    {
        _app.FocusTerminal();

        _app.Press(KeyboardKey.B, InputModifiers.Control);

        Assert.Equal("\u0002", _app.Terminal.SentText);
        Assert.False(_app.CollapseState.IsCollapsed.Value);
    }

    [Fact]
    public void WithoutTerminalFocus_CtrlB_StillCollapsesTheRepoBar()
    {
        _app.HoverTerminalWithoutFocus();

        _app.Press(KeyboardKey.B, InputModifiers.Control);

        Assert.True(_app.CollapseState.IsCollapsed.Value);
        Assert.Empty(_app.Terminal.Sent);
    }

    [Fact]
    public void AFocusedTerminal_TakesCtrlK_SoTheAppKeybindLayerNeverSeesIt()
    {
        _app.FocusTerminal();

        _app.Press(KeyboardKey.K, InputModifiers.Control);

        Assert.Equal("\u000b", _app.Terminal.SentText);
        Assert.False(_app.AppSaw(KeyboardKey.K, InputModifiers.Control));
    }

    [Fact]
    public void AFocusedTerminal_TakesEscape_SoAnOpenAssistantOverlayWouldNotClose()
    {
        _app.FocusTerminal();

        _app.Press(KeyboardKey.Escape);

        Assert.Equal("\u001b", _app.Terminal.SentText);
        Assert.False(_app.AppSaw(KeyboardKey.Escape, InputModifiers.None));
    }

    [Fact]
    public void WithoutTerminalFocus_EscapeReachesTheAppKeybindLayer()
    {
        _app.HoverTerminalWithoutFocus();

        _app.Press(KeyboardKey.Escape);

        Assert.True(_app.AppSaw(KeyboardKey.Escape, InputModifiers.None));
    }

    [Fact]
    public void AFocusedTerminal_TakesCtrlC_AsAnInterruptRatherThanACopy()
    {
        _app.FocusTerminal();

        _app.Press(KeyboardKey.C, InputModifiers.Control);

        Assert.Equal("\u0003", _app.Terminal.SentText);
        Assert.False(_app.AppSaw(KeyboardKey.C, InputModifiers.Control));
    }

    // ---- chords the terminal declines, asserted on what the application then does ----

    [Fact]
    public void AFocusedTerminal_TakesF5_AndTheForcedRefreshDoesNotRun()
    {
        // F5 is neither mode switching nor a repo hotkey, so it is outside the reserved set and the
        // program in the pane gets it. The refresh costs a click elsewhere first; the alternative
        // costs every full-screen program its F5, permanently and with no way to send one.
        _app.FocusTerminal();

        _app.Press(KeyboardKey.F5);

        Assert.Equal("\u001b[15~", _app.Terminal.SentText);
        Assert.Equal(0, _app.RefsChanged);
    }

    [Fact]
    public void AFocusedTerminal_DeclinesCtrlDigit_AndTheRepoStillSwitches()
    {
        _app.AssignHotkey(_app.SecondRepo, slot: 3);
        _app.FocusTerminal();

        _app.Press(KeyboardKey.Alpha3, InputModifiers.Control);

        Assert.Equal(_app.SecondRepo, _app.Registry.Active.Value?.Id);
        Assert.Empty(_app.Terminal.Sent);
    }

    [Fact]
    public void AFocusedTerminal_DeclinesCtrlNumpadDigit_AndTheRepoStillSwitches()
    {
        _app.AssignHotkey(_app.SecondRepo, slot: 3);
        _app.FocusTerminal();

        _app.Press(KeyboardKey.Numpad3, InputModifiers.Control);

        Assert.Equal(_app.SecondRepo, _app.Registry.Active.Value?.Id);
        Assert.Empty(_app.Terminal.Sent);
    }

    [Fact]
    public void AFocusedTerminal_DeclinesAnySuperChord_SoTheWindowManagerKeepsIt()
    {
        _app.FocusTerminal();

        _app.Press(KeyboardKey.B, InputModifiers.Super);

        Assert.Empty(_app.Terminal.Sent);
        Assert.True(_app.AppSaw(KeyboardKey.B, InputModifiers.Super));
    }

    // ---- the precondition nobody states ----

    [Fact]
    public void AKeyTheTerminalDeclines_ReachesTheAppOnlyWhileSomethingUnderThePointerIsHovered()
    {
        // The focus queue is built by hit-testing from the cursor, not from registration, so "the
        // terminal declined it" is necessary but not sufficient for an application keybind to run.
        _app.AssignHotkey(_app.SecondRepo, slot: 3);
        _app.FocusTerminal();
        _app.MovePointerOffEveryController();

        _app.Press(KeyboardKey.Alpha3, InputModifiers.Control);

        Assert.Equal(_app.FirstRepo, _app.Registry.Active.Value?.Id);
    }

    // ---- releases and modifier keys, against the app ----

    [Fact]
    public void AKeyRelease_IsNeverClaimedByTheTerminal_SoTheAppKeybindLayerStillSeesIt()
    {
        _app.FocusTerminal();

        _app.Release(KeyboardKey.Escape);

        Assert.Empty(_app.Terminal.Sent);
        Assert.True(_app.AppSaw(KeyboardKey.Escape, InputModifiers.None));
    }

    [Theory]
    [InlineData(KeyboardKey.LeftShift)]
    [InlineData(KeyboardKey.LeftControl)]
    [InlineData(KeyboardKey.LeftAlt)]
    [InlineData(KeyboardKey.LeftSuper)]
    public void HoldingAModifierAlone_SendsNothingAndIsNotClaimed(KeyboardKey key)
    {
        _app.FocusTerminal();

        var claim = _app.Press(key);

        Assert.Empty(_app.Terminal.Sent);
        Assert.Equal(KeyClaim.None, claim);
    }

    /// <summary>
    /// The application shell and the terminal pane in one input system, with the application's own
    /// keybind controller wired to real state so its effects can be read back.
    /// </summary>
    private sealed class CollidingApp : IDisposable
    {
        private const int PaneWidth = 800;
        private const int PaneHeight = 600;

        private readonly TempDir _dir = new("gitbench-terminal-seam-");
        private readonly PreferencesService _preferences;
        private readonly RepoRegistry _registry;
        private readonly MessageBus _bus = new();
        private readonly AssistantViewModel _assistant;
        private readonly AppReachSpy _spy = new();

        public CollidingApp()
        {
            var statePath = Path.Combine(_dir.Path, "state.json");
            _preferences = new PreferencesService(new Preferences(), Path.Combine(_dir.Path, "prefs.json"));
            CollapseState = new RepoBarCollapseState(_preferences);
            _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);

            FirstRepo = OpenRepo("first");
            SecondRepo = OpenRepo("second");
            _registry.SetActive(FirstRepo);

            _bus.Subscribe<RefsChangedMessage>(_ => RefsChanged++);
            _bus.Subscribe<WorkingTreeChangedMessage>(_ => WorkingTreeChanged++);

            var localization = new LocalizationService(new State<Locale>(Locale.En));
            _assistant = new AssistantViewModel(new StubAssistantStore(), localization, _bus);

            var keybind = new AppKeybindController(
                _registry, new RepoHoverState(), CollapseState, localization, _bus, _assistant);

            Harness = GuiTestHarness.Create(
                ctx =>
                {
                    var input = ctx.Require<InputSystem>();
                    Grid = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>())
                    {
                        Width = PaneWidth,
                        Height = PaneHeight,
                    };
                    Controller = new TerminalInputController(Grid, input, Terminal);

                    // The spy is registered before the keybind, so it sees - without consuming -
                    // everything that reaches the application layer at all.
                    var root = new Stack
                    {
                        Children = [new Raw { View = Grid }.WithController(input, () => Controller)],
                    }
                        .WithController(input, () => _spy)
                        .WithController(input, () => keybind);

                    return root.BuildView(ctx);
                },
                width: PaneWidth,
                height: PaneHeight,
                configure: ctx =>
                {
                    ctx.AddService<IThemeService<ThemeStyles>>(
                        new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                    ctx.AddService<ILocalizationService>(localization);
                });
        }

        public GuiTestHarness Harness { get; }
        public TerminalGridView Grid { get; private set; } = null!;
        public TerminalInputController Controller { get; private set; } = null!;
        public SeamTerminal Terminal { get; } = new();
        public RepoBarCollapseState CollapseState { get; }
        public IRepoRegistry Registry => _registry;
        public Guid FirstRepo { get; }
        public Guid SecondRepo { get; }
        public int RefsChanged { get; private set; }
        public int WorkingTreeChanged { get; private set; }

        public void FocusTerminal()
        {
            Harness.Click(PaneWidth / 2f, PaneHeight / 2f);
            Terminal.Clear();
            _spy.Clear();
        }

        /// <summary>The pointer over the pane but the keyboard never taken - the state before the
        /// user's first click, which is where every application keybind has to keep working.</summary>
        public void HoverTerminalWithoutFocus()
        {
            Harness.MoveTo(PaneWidth / 2f, PaneHeight / 2f);
            Terminal.Clear();
            _spy.Clear();
        }

        public void MovePointerOffEveryController() => Harness.MoveTo(-50f, -50f);

        public KeyClaim Press(KeyboardKey key, InputModifiers modifiers = InputModifiers.None) =>
            Send(key, modifiers, InputState.Pressed);

        public KeyClaim Release(KeyboardKey key, InputModifiers modifiers = InputModifiers.None) =>
            Send(key, modifiers, InputState.Released);

        // Built and dispatched by hand rather than through PressKey: the harness discards the event,
        // and the claim is half of what the reserved set means.
        private KeyClaim Send(KeyboardKey key, InputModifiers modifiers, InputState state)
        {
            var e = new KeyboardKeyEvent
            {
                Key = key,
                State = state,
                Modifiers = modifiers,
                Phase = EventPhase.Capturing,
            };
            Harness.Input.SendKeyboardKeyEvent(ref e);
            return e.Claim;
        }

        public bool AppSaw(KeyboardKey key, InputModifiers modifiers) => _spy.Saw(key, modifiers);

        public void AssignHotkey(Guid repoId, int slot) => _registry.AssignHotkey(repoId, slot);

        private Guid OpenRepo(string name)
        {
            var path = Path.Combine(_dir.Path, name);
            Directory.CreateDirectory(Path.Combine(path, ".git"));
            _registry.Open(path);
            return _registry.Active.Value!.Id;
        }

        public void Dispose()
        {
            Harness.Dispose();
            _assistant.Dispose();
            _preferences.Dispose();
            _dir.Dispose();
        }
    }
}

/// <summary>
/// The mode switcher's keep-alive swap against the terminal's keyboard: the pane stays mounted and
/// its shell keeps running when the user switches to History, so the keyboard has to be given up by
/// the pane itself - nothing unmounts it and nothing blurs it.
/// </summary>
public class TerminalModeSwitchSeamTests
{
    [Fact]
    public void SwitchingAwayFromTheTerminal_HidesItsBranchButLeavesTheViewMounted()
    {
        using var app = new SwitchedApp();
        app.FocusTerminal();

        app.Mode.Value = MainViewMode.History;

        Assert.False(app.BranchRoot.IsVisible);
        Assert.Same(app.Harness.Root, app.BranchRoot.Parent);
    }

    [Fact]
    public void AHiddenTerminal_StopsTakingKeys()
    {
        using var app = new SwitchedApp();
        app.FocusTerminal();
        app.Mode.Value = MainViewMode.History;

        var claim = app.Press(KeyboardKey.A);

        Assert.Empty(app.Terminal.Sent);
        Assert.Equal(KeyClaim.None, claim);
    }

    [Fact]
    public void AHiddenTerminal_ReleasesTheKeyboardItWasStillHolding()
    {
        using var app = new SwitchedApp();
        app.FocusTerminal();
        app.Mode.Value = MainViewMode.History;

        app.Press(KeyboardKey.A);

        Assert.Null(app.Harness.Input.FocusedComponent);
    }

    [Fact]
    public void AHiddenTerminal_LetsTheApplicationKeybindThroughOnTheSameKeystroke()
    {
        // The blur and the fall-through have to happen in one dispatch: releasing focus without
        // consuming lets the event continue down the focus queue immediately, so the user does not
        // lose the first key they press after switching modes.
        using var app = new SwitchedApp();
        app.FocusTerminal();
        app.Mode.Value = MainViewMode.History;

        app.Press(KeyboardKey.B, InputModifiers.Control);

        Assert.True(app.AppSaw(KeyboardKey.B, InputModifiers.Control));
    }

    [Fact]
    public void SwitchingBackToTheTerminal_ShowsTheSameViewRatherThanBuildingAFreshOne()
    {
        using var app = new SwitchedApp();
        var original = app.Grid;

        app.Mode.Value = MainViewMode.History;
        app.Mode.Value = MainViewMode.Terminal;

        Assert.Same(original, app.Grid);
        Assert.True(app.BranchRoot.IsVisible);
    }

    [Fact]
    public void SwitchingBackToTheTerminal_DoesNotHandTheKeyboardBack_TheUserHasToClickAgain()
    {
        using var app = new SwitchedApp();
        app.FocusTerminal();
        app.Mode.Value = MainViewMode.History;
        app.Press(KeyboardKey.A);
        app.Mode.Value = MainViewMode.Terminal;
        app.Terminal.Clear();

        app.Press(KeyboardKey.Enter);

        Assert.Empty(app.Terminal.Sent);
    }

    [Fact]
    public void ClickingTheTerminalAfterSwitchingBack_ResumesTyping()
    {
        using var app = new SwitchedApp();
        app.FocusTerminal();
        app.Mode.Value = MainViewMode.History;
        app.Press(KeyboardKey.A);
        app.Mode.Value = MainViewMode.Terminal;

        app.FocusTerminal();
        app.Press(KeyboardKey.Enter);

        Assert.Equal("\r", app.Terminal.SentText);
    }

    [Fact]
    public void ATerminalHiddenByAnAncestor_AlsoStopsTakingKeys()
    {
        // The keep-alive swap hides the branch ROOT. Today that root is the grid view itself, so a
        // check of the view's own IsVisible happens to be right; wrap the grid in any container - a
        // padding, a border, a status strip above it - and the grid stays IsVisible == true while
        // nothing of it is on screen. Hit-testing already walks ancestors, so this is the
        // controller walking ancestors rather than a stricter rule. It is not all of hit-testing, which
        // also clips against scrolling ancestors - not reachable while the pane is in no scroll host.
        using var app = new SwitchedApp(wrapTerminalInAContainer: true);
        app.FocusTerminal();
        app.Mode.Value = MainViewMode.History;

        var claim = app.Press(KeyboardKey.A);

        Assert.Empty(app.Terminal.Sent);
        Assert.Equal(KeyClaim.None, claim);
    }

    [Fact]
    public void InSwapModeTheControllerUnregistersOnUnmount_WhichIsWhyKeepAliveNeedsTheHiddenCheck()
    {
        using var app = new SwitchedApp(keepAlive: false);
        app.FocusTerminal();

        app.Mode.Value = MainViewMode.History;

        Assert.Null(app.Harness.Input.FocusedComponent);
        Assert.False(app.Harness.Input.IsInteractable(app.Controller));
    }

    /// <summary>
    /// A mode switcher with a live terminal branch: the real switch in keep-alive mode, the real
    /// input system, and a stand-in for the application's keybind layer above it.
    /// </summary>
    private sealed class SwitchedApp : IDisposable
    {
        private const int PaneWidth = 800;
        private const int PaneHeight = 600;

        private readonly AppReachSpy _spy = new();
        private readonly AppReachSpy _historyBranch = new();

        public SwitchedApp(bool keepAlive = true, bool wrapTerminalInAContainer = false)
        {
            Harness = GuiTestHarness.Create(
                ctx =>
                {
                    var input = ctx.Require<InputSystem>();
                    Grid = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>())
                    {
                        Width = PaneWidth,
                        Height = PaneHeight,
                    };
                    Controller = new TerminalInputController(Grid, input, Terminal);

                    IWidget terminalBranch =
                        new Raw { View = Grid }.WithController(input, () => Controller);
                    if (wrapTerminalInAContainer)
                        terminalBranch = new Stack
                        {
                            Width = PaneWidth,
                            Height = PaneHeight,
                            Children = [terminalBranch],
                        };

                    var root = new Switch<MainViewMode>
                    {
                        Value = Mode,
                        KeepAlive = keepAlive,
                        Case = m => m switch
                        {
                            MainViewMode.Terminal => terminalBranch,
                            _ => new Stack { Width = PaneWidth, Height = PaneHeight }
                                .WithController(input, () => _historyBranch),
                        },
                    }.WithController(input, () => _spy);

                    return root.BuildView(ctx);
                },
                width: PaneWidth,
                height: PaneHeight,
                configure: ctx => ctx.AddService<IThemeService<ThemeStyles>>(
                    new ThemeService(new State<ThemeMode>(ThemeMode.Dark))));
        }

        public State<MainViewMode> Mode { get; } = new(MainViewMode.Terminal);
        public GuiTestHarness Harness { get; }
        public TerminalGridView Grid { get; private set; } = null!;
        public TerminalInputController Controller { get; private set; } = null!;
        public SeamTerminal Terminal { get; } = new();

        /// <summary>The view the keep-alive swap toggles: the switch host's own child, which is the
        /// grid itself unless the branch was deliberately wrapped.</summary>
        public View BranchRoot
        {
            get
            {
                View view = Grid;
                while (view.Parent is { } parent && parent != Harness.Root)
                    view = parent;
                return view;
            }
        }

        public void FocusTerminal()
        {
            Harness.Click(PaneWidth / 2f, PaneHeight / 2f);
            Terminal.Clear();
            _spy.Clear();
        }

        public KeyClaim Press(KeyboardKey key, InputModifiers modifiers = InputModifiers.None)
        {
            var e = new KeyboardKeyEvent
            {
                Key = key,
                State = InputState.Pressed,
                Modifiers = modifiers,
                Phase = EventPhase.Capturing,
            };
            Harness.Input.SendKeyboardKeyEvent(ref e);
            return e.Claim;
        }

        public bool AppSaw(KeyboardKey key, InputModifiers modifiers) => _spy.Saw(key, modifiers);

        public void Dispose() => Harness.Dispose();
    }
}

/// <summary>
/// The terminal against everything else that takes the keyboard: a dialog, a field, another list.
/// </summary>
public class TerminalFocusArbitrationTests : IDisposable
{
    // The pane is the upper half. Window Y runs up, so the pane is the higher band.
    private const int WindowWidth = 800;
    private const int WindowHeight = 600;
    private const float InsideThePane = 450f;
    private const float BelowThePane = 100f;

    private readonly GuiTestHarness _harness;
    private readonly SeamTerminal _terminal = new();
    private readonly FocusThief _thief = new();
    private TerminalGridView _grid = null!;
    private TerminalInputController _controller = null!;

    public TerminalFocusArbitrationTests()
    {
        _harness = GuiTestHarness.Create(
            ctx =>
            {
                var input = ctx.Require<InputSystem>();
                _grid = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>())
                {
                    Width = WindowWidth,
                    Height = WindowHeight / 2f,
                };
                _controller = new TerminalInputController(_grid, input, _terminal);
                _thief.Input = input;

                return new Column
                {
                    Children =
                    [
                        new Raw { View = _grid }.WithController(input, () => _controller),
                        new Stack { Width = WindowWidth, Height = WindowHeight / 2f }
                            .WithController(input, () => _thief),
                    ],
                }.BuildView(ctx);
            },
            width: WindowWidth,
            height: WindowHeight,
            configure: ctx => ctx.AddService<IThemeService<ThemeStyles>>(
                new ThemeService(new State<ThemeMode>(ThemeMode.Dark))));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ClickingThePane_TakesTheKeyboardWithoutSwallowingTheClick()
    {
        _harness.Click(400f, InsideThePane);

        Assert.Same(_controller, _harness.Input.FocusedComponent);
        Assert.Equal(0, _thief.Presses);
    }

    [Fact]
    public void AnotherControlTakingTheKeyboard_StopsTheTerminalTyping()
    {
        _harness.Click(400f, InsideThePane);
        _terminal.Clear();

        _harness.Click(400f, BelowThePane);
        Press(KeyboardKey.Enter);

        Assert.NotSame(_controller, _harness.Input.FocusedComponent);
        Assert.Empty(_terminal.Sent);
    }

    [Fact]
    public void HoveringBackOverThePane_DoesNotHandTheKeyboardBack()
    {
        _harness.Click(400f, InsideThePane);
        _harness.Click(400f, BelowThePane);
        _terminal.Clear();

        _harness.MoveTo(400f, InsideThePane);
        Press(KeyboardKey.Enter);

        Assert.Empty(_terminal.Sent);
    }

    [Fact]
    public void ClickingBackOnThePane_ResumesTyping()
    {
        _harness.Click(400f, InsideThePane);
        _harness.Click(400f, BelowThePane);
        _terminal.Clear();

        _harness.Click(400f, InsideThePane);
        Press(KeyboardKey.Enter);

        Assert.Equal("\r", _terminal.SentText);
    }

    [Fact]
    public void AControlThatStealsTheKeyboardWithoutAClick_AlsoStopsTheTerminalTyping()
    {
        // A dialog opening over the pane, or a rename field auto-focusing: the terminal is never
        // told, it simply stops being the focused component.
        _harness.Click(400f, InsideThePane);
        _terminal.Clear();

        _thief.TakeFocus();
        Press(KeyboardKey.Enter);

        Assert.Empty(_terminal.Sent);
    }

    [Fact]
    public void ClickingThePaneTwice_IsIdempotentAndSendsNothingOfItsOwn()
    {
        _harness.Click(400f, InsideThePane);
        _harness.Click(400f, InsideThePane);
        _terminal.Clear();

        Press(KeyboardKey.Enter);

        Assert.Equal("\r", _terminal.SentText);
    }

    private void Press(KeyboardKey key, InputModifiers modifiers = InputModifiers.None)
    {
        var e = new KeyboardKeyEvent
        {
            Key = key,
            State = InputState.Pressed,
            Modifiers = modifiers,
            Phase = EventPhase.Capturing,
        };
        _harness.Input.SendKeyboardKeyEvent(ref e);
    }
}

/// <summary>
/// The view model as the thing a keyboard writes to: when it takes input, when it stops, and what
/// happens to a keystroke that arrives on either side of those edges.
/// </summary>
public class TerminalInputLifecycleTests
{
    [Fact]
    public void BeforeTheShellIsAdopted_TheViewModelTakesNoInput()
    {
        using var run = TerminalRun.NotYetStarted();

        Assert.False(run.Vm.IsAcceptingInput);
    }

    [Fact]
    public void BeforeTheShellIsAdopted_SendingInputIsANoOpRatherThanAThrow()
    {
        using var run = TerminalRun.NotYetStarted();

        run.Vm.SendInput("x"u8);

        Assert.Empty(run.Pty.Input);
    }

    [Fact]
    public void BeforeTheShellIsAdopted_TheModesAreTheOnesThatEncodeNothingExotic()
    {
        using var run = TerminalRun.NotYetStarted();

        Assert.False(run.Vm.Modes.ApplicationCursorKeys);
    }

    [Fact]
    public void OnceTheShellIsRunning_InputReachesThePseudoTerminal()
    {
        using var run = TerminalRun.Started();

        run.Vm.SendInput("hi"u8);

        Assert.Equal("hi"u8.ToArray(), run.Pty.Input);
    }

    [Fact]
    public void AfterDispose_TheViewModelStopsAcceptingInput()
    {
        using var run = TerminalRun.Started();

        run.Vm.Dispose();

        Assert.False(run.Vm.IsAcceptingInput);
    }

    [Fact]
    public void AfterDispose_SendingInputIsANoOpRatherThanAThrow()
    {
        using var run = TerminalRun.Started();
        run.Vm.Dispose();

        run.Vm.SendInput("x"u8);

        Assert.Empty(run.Pty.Input);
    }

    [Fact]
    public void AFailedStart_TakesNoInputAndSwallowsWhatIsSentAnyway()
    {
        using var run = TerminalRun.ThatFailsToStart();

        run.Vm.SendInput("x"u8);

        Assert.False(run.Vm.IsAcceptingInput);
        Assert.Empty(run.Pty.Input);
    }

    [Fact]
    public void AShellThatExitsOnItsOwn_StopsAcceptingWhatIsSentToIt()
    {
        // Otherwise the pane goes on claiming every key in the window for a shell that is gone, and
        // the only way out is noticing that clicking elsewhere helps.
        using var run = TerminalRun.Started();
        run.Pty.ShellExits();
        run.WaitFor(() => !run.Vm.IsAcceptingInput, "the pane to notice the shell is gone");

        run.Vm.SendInput("x"u8);

        Assert.Empty(run.Pty.Input);
    }

    [Fact]
    public void TypingWhileADrainIsStillQueued_ReachesTheShellAndDoesNotLoseThePendingOutput()
    {
        using var run = TerminalRun.Started();
        run.Pty.Emit("before");
        run.WaitForPendingDrain();

        run.Vm.SendInput("x"u8);
        run.Dispatcher.Drain();

        Assert.Equal("x"u8.ToArray(), run.Pty.Input);
        Assert.Equal("before", run.RowText(0));
    }

    [Fact]
    public void DisposingWhileADrainIsQueued_DropsTheDrainInsteadOfFeedingADisposedEngine()
    {
        using var run = TerminalRun.Started();
        run.Pty.Emit("late");
        run.WaitForPendingDrain();

        run.Vm.Dispose();
        run.Dispatcher.Drain();

        Assert.False(run.Vm.IsAcceptingInput);
    }
}

/// <summary>
/// The join between the engine's modes and the key encoding: the same arrow key has to encode
/// differently once a program sets DECCKM, which is only true if the modes are read at the moment
/// the key is pressed.
/// </summary>
public class TerminalLiveModeSeamTests
{
    private const string Esc = "\u001b";
    private const string Csi = Esc + "[";
    private const string Ss3 = Esc + "O";

    [Fact]
    public void WithApplicationCursorKeysOff_TheUpArrowIsACsiSequence()
    {
        using var run = TerminalRun.Started();

        Assert.Equal(Csi + "A", Encode(run, TerminalKey.Up));
    }

    [Fact]
    public void AProgramSettingDeccKmMidSession_ChangesWhatTheSameArrowKeyEncodes()
    {
        using var run = TerminalRun.Started();
        var before = Encode(run, TerminalKey.Up);

        run.Pty.Emit(Csi + "?1h");
        run.WaitFor(() => run.Vm.Modes.ApplicationCursorKeys, "the program to set DECCKM");
        var after = Encode(run, TerminalKey.Up);

        Assert.Equal(Csi + "A", before);
        Assert.Equal(Ss3 + "A", after);
    }

    [Fact]
    public void AProgramClearingDeccKmMidSession_PutsTheArrowKeyBackToCsi()
    {
        using var run = TerminalRun.Started();
        run.Pty.Emit(Csi + "?1h");
        run.WaitFor(() => run.Vm.Modes.ApplicationCursorKeys, "the program to set DECCKM");

        run.Pty.Emit(Csi + "?1l");
        run.WaitFor(() => !run.Vm.Modes.ApplicationCursorKeys, "the program to clear DECCKM");

        Assert.Equal(Csi + "A", Encode(run, TerminalKey.Up));
    }

    [Fact]
    public void UnderApplicationCursorKeys_AModifiedArrowIsStillCsi()
    {
        using var run = TerminalRun.Started();
        run.Pty.Emit(Csi + "?1h");
        run.WaitFor(() => run.Vm.Modes.ApplicationCursorKeys, "the program to set DECCKM");

        Assert.Equal(Csi + "1;5A", Encode(run, TerminalKey.Up, TerminalKeyModifiers.Ctrl));
    }

    [Fact]
    public void AControllerBuiltBeforeTheShellExisted_StillTypesAgainstTheShellsLiveModes()
    {
        // The pane builds its controller inside UseViewModel, while the view model is still
        // Starting - before there is a session, let alone a mode to capture.
        using var run = TerminalRun.NotYetStarted();
        var input = new InputSystem();
        var view = new View { Width = 100f, Height = 100f };
        var controller = new TerminalInputController(view, input, run.Vm);
        input.RegisterController(view, controller);
        input.StealFocus(controller);

        run.Start();
        run.Pty.Emit(Csi + "?1h");
        run.WaitFor(() => run.Vm.Modes.ApplicationCursorKeys, "the program to set DECCKM");
        var e = new KeyboardKeyEvent
        {
            Key = KeyboardKey.UpArrow,
            State = InputState.Pressed,
            Modifiers = InputModifiers.None,
            Phase = EventPhase.Capturing,
        };
        input.SendKeyboardKeyEvent(ref e);

        Assert.Equal(Ss3 + "A", Encoding.UTF8.GetString(run.Pty.Input));
    }

    // The encoder is handed the view model's live modes, which is what the controller does.
    private static string Encode(
        TerminalRun run,
        TerminalKey key,
        TerminalKeyModifiers modifiers = TerminalKeyModifiers.None)
    {
        Span<byte> buffer = stackalloc byte[TerminalKeyEncoder.MaxEncodedBytes];
        TerminalKeyEncoder.Encode(key, modifiers, run.Vm.Modes, buffer, out var written);
        return Encoding.UTF8.GetString(buffer[..written]);
    }
}

/// <summary>
/// What attaching a focus-stealing controller to the grid view costs the parts of the pane that
/// were already working: the renderer, and the replayed-recording launch.
/// </summary>
public class TerminalInputRegressionTests
{
    private const int PaneWidth = 800;
    private const int PaneHeight = 600;

    // 800/8 and 600/16: what an 800x600 pane is worth in cells under the synthetic measurer.
    private static readonly TerminalSize ExpectedViewport = new(100, 37);

    [Fact]
    public void AGridViewWithAnInputControllerAttached_StillDrawsTheScreen()
    {
        using var run = TerminalRun.Replaying("hello");
        using var harness = Harness(run, out _);

        var canvas = harness.Render();

        Assert.Contains(canvas.GlyphRuns, r => r.Text.StartsWith("hello"));
    }

    [Fact]
    public void AGridViewWithAnInputControllerAttached_StillReportsItsViewportInCells()
    {
        using var run = TerminalRun.Replaying("hello");
        TerminalSize? reported = null;
        using var harness = Harness(run, out var grid);
        grid.OnViewportChanged = size => reported = size;

        harness.Render();

        Assert.Equal(ExpectedViewport, reported);
    }

    [Fact]
    public void TypingIntoAReplayedRecording_IsSwallowedRatherThanThrowing()
    {
        // A recording's pseudo-terminal has nowhere to put input and its stream has already ended
        // by the time anything is drawn. A keystroke must not become an exception out of dispatch.
        using var run = TerminalRun.Replaying("hello");
        using var harness = Harness(run, out _);
        harness.Render();

        harness.Click(PaneWidth / 2f, PaneHeight / 2f);
        harness.Type("q");
        harness.PressKey(KeyboardKey.Enter);

        Assert.Contains(harness.Render().GlyphRuns, r => r.Text.StartsWith("hello"));
    }

    [Fact]
    public void AReplayedRecording_KeepsItsPinnedSizeEvenThoughThePaneNowTakesInput()
    {
        using var run = TerminalRun.Replaying("hello");
        using var harness = Harness(run, out _);

        harness.Render();
        harness.Resize(400, 300);
        harness.Render();

        Assert.Equal(ExpectedViewport, run.Session!.Grid.Size);
    }

    private static GuiTestHarness Harness(TerminalRun run, out TerminalGridView grid)
    {
        TerminalGridView? built = null;

        var harness = GuiTestHarness.Create(
            ctx =>
            {
                var input = ctx.Require<InputSystem>();
                built = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>())
                {
                    Width = PaneWidth,
                    Height = PaneHeight,
                };
                built.SetRenderState(new TerminalRenderState.Running(run.Session!));
                var controller = new TerminalInputController(built, input, run.Vm);
                return new Raw { View = built }
                    .WithController(input, () => controller)
                    .BuildView(ctx);
            },
            width: PaneWidth,
            height: PaneHeight,
            configure: ctx => ctx.AddService<IThemeService<ThemeStyles>>(
                new ThemeService(new State<ThemeMode>(ThemeMode.Dark))));

        grid = built!;
        return harness;
    }
}

/// <summary>
/// The whole feature against a real shell on a real pseudo-terminal: keys typed through the
/// controller, turned into bytes by the encoder, written to the shell, and read back off the grid.
/// </summary>
/// <remarks>
/// Excluded from a default run by its trait - it spawns a process, and the shell command has no
/// implementation to pick on a host without one.
/// </remarks>
[Trait("Category", "LiveShell")]
public class TerminalLiveShellRoundTripTests
{
    private const int PaneWidth = 800;
    private const int PaneHeight = 600;
    private const int TimeoutSeconds = 60;

    private static bool CanSpawnAShell => OperatingSystem.IsWindows();

    [Fact]
    public void TypingEchoAndPressingEnter_PutsTheShellsAnswerOnTheScreen()
    {
        if (!CanSpawnAShell) return;

        using var dir = new TempDir("gitbench-terminal-live-");
        var dispatcher = new QueuedDispatcher();
        using var session = TerminalSession.Start(
            new PtySessionFactory(),
            new XtermSharpEngineFactory(),
            ShellCommand.For(dir.Path, new PtySize(100, 37)),
            dispatcher);

        var terminal = new LiveTerminal(session);
        using var harness = GuiTestHarness.Create(
            ctx =>
            {
                var input = ctx.Require<InputSystem>();
                var grid = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>())
                {
                    Width = PaneWidth,
                    Height = PaneHeight,
                };
                grid.SetRenderState(new TerminalRenderState.Running(session));
                var controller = new TerminalInputController(grid, input, terminal);
                return new Raw { View = grid }
                    .WithController(input, () => controller)
                    .BuildView(ctx);
            },
            width: PaneWidth,
            height: PaneHeight,
            configure: ctx => ctx.AddService<IThemeService<ThemeStyles>>(
                new ThemeService(new State<ThemeMode>(ThemeMode.Dark))));

        harness.Render();
        harness.Click(PaneWidth / 2f, PaneHeight / 2f);

        // A prompt first: typing into a shell that has not finished starting loses the line.
        Pump.WaitFor(dispatcher, () => GridText.Any(session.Grid),
            "the shell to draw a prompt", TimeoutSeconds);

        harness.Type("echo seam-ok");
        harness.PressKey(KeyboardKey.Enter);

        Pump.WaitFor(
            dispatcher,
            () => GridText.HasWholeRow(session.Grid, "seam-ok"),
            "the shell to echo the line back on a row of its own",
            TimeoutSeconds);
    }
}

// ------------------------------- shared seam scaffolding -------------------------------

/// <summary>
/// A terminal view model over a scriptable pseudo-terminal and a real engine, with the dispatcher
/// the test pumps by hand - so "the program set a mode" and "a key was pressed" are ordered rather
/// than raced.
/// </summary>
internal sealed class TerminalRun : IDisposable
{
    private static readonly TerminalSize Viewport = new(100, 37);

    private readonly SeamLaunch _launch;

    private TerminalRun(SeamLaunch launch)
    {
        _launch = launch;
        Vm = new TerminalViewModel(launch, Dispatcher);
    }

    public QueuedDispatcher Dispatcher { get; } = new();
    public TerminalViewModel Vm { get; }
    public SeamPty Pty => _launch.Pty;

    /// <summary>The live session, read the way the pane reads it: off the render state.</summary>
    public TerminalSession? Session =>
        Vm.RenderState.Value is TerminalRenderState.Running running ? running.Session : null;

    public static TerminalRun NotYetStarted() => new(new SeamLaunch());

    public static TerminalRun Started()
    {
        var run = new TerminalRun(new SeamLaunch());
        run.Start();
        return run;
    }

    public static TerminalRun ThatFailsToStart()
    {
        var run = new TerminalRun(new SeamLaunch { FailWith = "no shell here" });
        run.Vm.ReportViewport(Viewport);
        Pump.WaitFor(
            run.Dispatcher,
            () => run.Vm.RenderState.Value is TerminalRenderState.Failed,
            "the start to fail");
        return run;
    }

    /// <summary>A replay launch instead of a shell: fixed bytes, fixed size, input goes nowhere.</summary>
    public static TerminalRun Replaying(string screen)
    {
        var run = new TerminalRun(new SeamLaunch
        {
            Recording = new TerminalRecording(Encoding.UTF8.GetBytes(screen), Viewport),
        });
        run.Start();
        Pump.WaitFor(
            run.Dispatcher,
            () => GridText.Any(run.Session!.Grid),
            "the recording to reach the screen");
        return run;
    }

    public void Start()
    {
        Vm.ReportViewport(Viewport);

        // Adoption, not acceptance. A replay whose bytes have run out is adopted and Running while
        // already reporting no live shell, so waiting on IsAcceptingInput here would hang on exactly
        // the fixtures that exercise a finished session.
        Pump.WaitFor(
            Dispatcher,
            () => Vm.RenderState.Value is TerminalRenderState.Running,
            "the shell to be adopted");
    }

    public void WaitFor(Func<bool> done, string what) => Pump.WaitFor(Dispatcher, done, what);

    /// <summary>Waits for the reader thread to have posted a drain without running it.</summary>
    public void WaitForPendingDrain() =>
        SpinWait.SpinUntil(() => Dispatcher.Queued > 0, TimeSpan.FromSeconds(5));

    public string RowText(int row) =>
        Session is { } session ? GridText.Row(session.Grid, row) : string.Empty;

    public void Dispose()
    {
        Vm.Dispose();
        Dispatcher.Drain();
    }
}

/// <summary>Starts whatever the test asked for: a scripted pseudo-terminal, a recording, or a
/// failure.</summary>
internal sealed class SeamLaunch : ITerminalLaunch
{
    public SeamPty Pty { get; } = new();

    public string? FailWith { get; init; }

    public TerminalRecording? Recording { get; init; }

    public TerminalSize SizeFor(TerminalSize viewport) => Recording?.Size ?? viewport;

    public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher)
    {
        if (FailWith is { } message) throw new InvalidOperationException(message);

        if (Recording is { } recording)
            return TerminalSession.Start(
                () => new RecordedPtySession(recording.Bytes),
                new XtermSharpEngineFactory(),
                recording.Size,
                dispatcher);

        return TerminalSession.Start(() => Pty, new XtermSharpEngineFactory(), size, dispatcher);
    }
}

/// <summary>
/// A pseudo-terminal whose program half a test writes: output pushed in when the test says so,
/// input kept for the test to read back.
/// </summary>
internal sealed class SeamPty : IPtySession
{
    private readonly BlockingCollection<byte[]> _output = new();
    private readonly List<byte> _input = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<PtyExit> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private byte[] _current = [];
    private int _offset;
    private volatile bool _closed;

    public Task<PtyExit> Exited => _exited.Task;

    public byte[] Input
    {
        get
        {
            lock (_gate) return _input.ToArray();
        }
    }

    /// <summary>The program writes to its terminal.</summary>
    public void Emit(string vt) => _output.Add(Encoding.UTF8.GetBytes(vt));

    /// <summary>The child finishes on its own and the terminal closes behind it, which is what makes
    /// a later write throw the way a real one does.</summary>
    public void ShellExits()
    {
        _closed = true;
        _output.CompleteAdding();
        _exited.TrySetResult(new PtyExit.Completed(0));
    }

    public int ReadOutput(Span<byte> buffer)
    {
        while (_offset >= _current.Length)
        {
            if (!TryTakeNext(out var next)) return 0;
            _current = next;
            _offset = 0;
        }

        var take = Math.Min(_current.Length - _offset, buffer.Length);
        _current.AsSpan(_offset, take).CopyTo(buffer);
        _offset += take;
        return take;
    }

    public void WriteInput(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        lock (_gate) _input.AddRange(bytes);
    }

    public void Resize(PtySize size) => ObjectDisposedException.ThrowIf(_closed, this);

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _output.CompleteAdding();
        _exited.TrySetResult(new PtyExit.TornDown());
    }

    private bool TryTakeNext(out byte[] next)
    {
        try
        {
            return _output.TryTake(out next!, Timeout.Infinite);
        }
        catch (InvalidOperationException)
        {
            // The collection was completed (or disposed) while this read was blocked: end of
            // stream, which is what a real terminal reports when its child is gone.
            next = [];
            return false;
        }
    }
}

/// <summary>The shell a controller writes to, reduced to what a test needs to read back.</summary>
internal sealed class SeamTerminal : ITerminalInput
{
    private readonly List<byte> _sent = [];

    public bool IsAcceptingInput { get; set; } = true;

    public TerminalModes Modes { get; set; }

    public byte[] Sent => _sent.ToArray();

    public string SentText => Encoding.UTF8.GetString(_sent.ToArray());

    public void Clear() => _sent.Clear();

    public void SendInput(ReadOnlySpan<byte> bytes) => _sent.AddRange(bytes);
}

/// <summary>A live session behind the narrow input interface, for the round-trip test.</summary>
internal sealed class LiveTerminal : ITerminalInput
{
    private readonly TerminalSession _session;

    public LiveTerminal(TerminalSession session) => _session = session;

    public bool IsAcceptingInput => true;

    public TerminalModes Modes => _session.State.Modes;

    public void SendInput(ReadOnlySpan<byte> bytes) => _session.Write(bytes);
}

/// <summary>
/// Stands where the application's keybinding layer stands and records what reaches it, consuming
/// nothing - so "the terminal declined this" and "the application got it" stay separate assertions.
/// </summary>
internal sealed class AppReachSpy : KeyboardMouseController
{
    private readonly List<(KeyboardKey Key, InputModifiers Modifiers)> _seen = [];

    public int Presses { get; private set; }

    public void Clear()
    {
        _seen.Clear();
        Presses = 0;
    }

    public bool Saw(KeyboardKey key, InputModifiers modifiers) => _seen.Contains((key, modifiers));

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.Phase == EventPhase.Capturing) _seen.Add((e.Key, e.Modifiers));
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase == EventPhase.Bubbling && e.State == InputState.Pressed) Presses++;
    }
}

/// <summary>A dialog or a text field: takes the keyboard and keeps every key it is given.</summary>
internal sealed class FocusThief : KeyboardMouseController
{
    public InputSystem? Input { get; set; }

    public int Presses { get; private set; }

    public void TakeFocus() => Input?.StealFocus(this);

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.State != InputState.Pressed || e.Phase != EventPhase.Bubbling) return;
        Presses++;
        TakeFocus();
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State == InputState.Pressed) e.Consume();
    }
}

/// <summary>Reads the screen back as text, so an assertion can be a line the user would have seen.</summary>
internal static class GridText
{
    public static string Row(ITerminalGrid grid, int row)
    {
        var cells = new TerminalCell[grid.Size.Columns];
        grid.CopyRow(row, cells);

        var text = new StringBuilder(cells.Length);
        foreach (var cell in cells)
        {
            if (cell.Width == CellWidth.WideTrailer) continue;
            text.Append(cell.Text);
        }

        return text.ToString().TrimEnd();
    }

    public static bool Any(ITerminalGrid grid)
    {
        for (var row = 0; row < grid.Size.Rows; row++)
            if (Row(grid, row).Length > 0) return true;
        return false;
    }

    public static bool HasWholeRow(ITerminalGrid grid, string text)
    {
        for (var row = 0; row < grid.Size.Rows; row++)
            if (Row(grid, row) == text) return true;
        return false;
    }
}

/// <summary>The assistant store reduced to what the assistant view model reads at construction, so
/// the application's keybind controller can be built without a live conversation behind it.</summary>
internal sealed class StubAssistantStore : IAssistantSessionStore
{
    public IReadable<AssistantSession?> Active { get; } = new State<AssistantSession?>(null);

    public IReadable<CommitMessageQuickAction?> CommitMessage { get; } =
        new State<CommitMessageQuickAction?>(null);

    public IReadable<AssistantSettings> Settings { get; } =
        new State<AssistantSettings>(AssistantSettings.Default);

    public IReadable<bool> IsConfigured { get; } = new State<bool>(true);

    public IReadable<AssistantKeyring> Keys { get; } =
        new State<AssistantKeyring>(AssistantKeyring.Empty);

    public void Save(AssistantSettings settings, string? apiKey)
    {
    }

    public void RunPreset(string agentName, string prompt)
    {
    }
}
