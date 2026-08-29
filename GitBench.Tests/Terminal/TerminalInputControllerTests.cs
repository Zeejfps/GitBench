using System.Collections.Concurrent;
using System.Text;
using GitBench.Features.Terminal;
using GitBench.Localization;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The terminal pane's keyboard, driven through the real input system: which keys become bytes on
/// the shell's input, which are claimed as text so the character still arrives, and which the pane
/// declines so the application's own keybindings still work.
/// </summary>
/// <remarks>
/// <para>
/// The capture point is a pseudo-terminal that records what reaches it, so an assertion is the bytes
/// the shell would have read — through the real <see cref="TerminalSession"/> rather than around it.
/// </para>
/// <para>
/// Escape characters are spelled as "\u001b" and never written literally: an escape in a source
/// literal is invisible in every diff and review that follows.
/// </para>
/// </remarks>
public class TerminalInputControllerTests
{
    const string Esc = "\u001b";
    const string Csi = Esc + "[";
    const string Ss3 = Esc + "O";

    // ---- focus ----

    [Fact]
    public void WhileTheProgramIsNotTrackingTheMouse_AClickTakesTheKeyboardWithoutSwallowingIt()
    {
        using var pane = Pane.Create();

        pane.Harness.Click(400f, 300f);

        Assert.Same(pane.Controller, pane.Harness.Input.FocusedComponent);
        Assert.True(pane.App.SawMousePress, "The click never reached the rest of the pane.");
    }

    // ---- keys that become bytes ----

    [Theory]
    [InlineData(KeyboardKey.Enter, InputModifiers.None, "\r")]
    [InlineData(KeyboardKey.NumpadEnter, InputModifiers.None, "\r")]
    [InlineData(KeyboardKey.Backspace, InputModifiers.None, "\u007f")]
    [InlineData(KeyboardKey.UpArrow, InputModifiers.None, Csi + "A")]
    [InlineData(KeyboardKey.LeftArrow, InputModifiers.Shift, Csi + "1;2D")]
    [InlineData(KeyboardKey.RightArrow, InputModifiers.Control, Csi + "1;5C")]
    [InlineData(KeyboardKey.Home, InputModifiers.None, Csi + "H")]
    [InlineData(KeyboardKey.Delete, InputModifiers.None, Csi + "3~")]
    [InlineData(KeyboardKey.PageUp, InputModifiers.None, Csi + "5~")]
    [InlineData(KeyboardKey.F1, InputModifiers.None, Ss3 + "P")]
    [InlineData(KeyboardKey.F12, InputModifiers.None, Csi + "24~")]
    public void AnEncodedKey_ReachesTheShellAsACommand(
        KeyboardKey key,
        InputModifiers modifiers,
        string expected)
    {
        using var pane = Pane.Focused();

        var claim = pane.Press(key, modifiers);

        Assert.Equal(expected, pane.Terminal.Text);
        Assert.Equal(KeyClaim.Command, claim);
    }

    [Theory]
    [InlineData(InputModifiers.NumLock)]
    [InlineData(InputModifiers.CapsLock)]
    public void AKeyboardLock_IsNotATerminalModifier(InputModifiers modifiers)
    {
        // Num lock is on for most of the world all of the time. Folding it into the modifier
        // parameter would turn every arrow key into a chord no program recognises.
        using var pane = Pane.Focused();

        pane.Press(KeyboardKey.RightArrow, modifiers);

        Assert.Equal(Csi + "C", pane.Terminal.Text);
    }

    [Fact]
    public void TheEncodingFollowsTheShellsModes_NotADefaultSetOfThem()
    {
        // DECCKM: the program has asked for application cursor keys, and the arrows change shape for
        // as long as it has.
        using var pane = Pane.Focused(TestTerminal.Live(Output(Csi + "?1h")));

        pane.Press(KeyboardKey.UpArrow);

        Assert.Equal(Ss3 + "A", pane.Terminal.Text);
    }

    // ---- keys the text pipeline carries ----

    [Fact]
    public void ATypeableKey_IsClaimedAsTextAndSendsNothingOfItsOwn()
    {
        using var pane = Pane.Focused();

        var claim = pane.Press(KeyboardKey.A);

        Assert.Empty(pane.Terminal.Written);
        Assert.Equal(KeyClaim.Text, claim);
    }

    [Theory]
    [InlineData("a", new byte[] { 0x61 })]
    [InlineData("\U0001F600", new byte[] { 0xF0, 0x9F, 0x98, 0x80 })]
    public void TypedText_ReachesTheShellAsUtf8(string typed, byte[] expected)
    {
        // Two rows only: the boundary table lives in TerminalInputEdgeTests against a stand-in
        // shell. What this one adds is the real session and a real pseudo-terminal underneath it.
        using var pane = Pane.Focused();

        pane.Harness.Type(typed);

        Assert.Equal(expected, pane.Terminal.Written);
    }

    // ---- keys that mean nothing here ----

    [Fact]
    public void AKeyRelease_IsNeitherEncodedNorClaimed()
    {
        using var pane = Pane.Focused();

        var claim = pane.Release(KeyboardKey.UpArrow);

        Assert.Empty(pane.Terminal.Written);
        Assert.Equal(KeyClaim.None, claim);
    }

    [Theory]
    [InlineData(KeyboardKey.LeftShift)]
    [InlineData(KeyboardKey.RightShift)]
    [InlineData(KeyboardKey.LeftControl)]
    [InlineData(KeyboardKey.RightControl)]
    [InlineData(KeyboardKey.LeftAlt)]
    [InlineData(KeyboardKey.RightAlt)]
    [InlineData(KeyboardKey.LeftSuper)]
    [InlineData(KeyboardKey.RightSuper)]
    public void AModifierKeyOnItsOwn_SendsNothingAndClaimsNothing(KeyboardKey key)
    {
        using var pane = Pane.Focused();

        var claim = pane.Press(key);

        Assert.Empty(pane.Terminal.Written);
        Assert.Equal(KeyClaim.None, claim);
    }

    // ---- the reserved set ----

    [Theory]
    [InlineData(KeyboardKey.Alpha1, InputModifiers.Control)]
    [InlineData(KeyboardKey.Alpha9, InputModifiers.Control)]
    [InlineData(KeyboardKey.Numpad1, InputModifiers.Control)]
    [InlineData(KeyboardKey.Numpad9, InputModifiers.Control)]
    [InlineData(KeyboardKey.P, InputModifiers.Super)]
    [InlineData(KeyboardKey.Alpha1, InputModifiers.Super)]
    [InlineData(KeyboardKey.LeftArrow, InputModifiers.Super)]
    public void AReservedChord_FallsThroughToTheApplication(
        KeyboardKey key,
        InputModifiers modifiers)
    {
        using var pane = Pane.Focused();

        var claim = pane.Press(key, modifiers);

        Assert.Empty(pane.Terminal.Written);
        Assert.Equal(KeyClaim.None, claim);
        Assert.Contains((key, modifiers), pane.App.Keys);
    }

    [Theory]
    [InlineData(KeyboardKey.Escape, InputModifiers.None, Esc)]
    [InlineData(KeyboardKey.Tab, InputModifiers.None, "\t")]
    [InlineData(KeyboardKey.UpArrow, InputModifiers.None, Csi + "A")]
    [InlineData(KeyboardKey.B, InputModifiers.Control, "\u0002")]
    [InlineData(KeyboardKey.K, InputModifiers.Control, "\u000b")]
    public void AChordTheApplicationWouldAlsoWant_IsNotReserved(
        KeyboardKey key,
        InputModifiers modifiers,
        string expected)
    {
        // Ctrl+B and Ctrl+K are the repo bar and the assistant elsewhere in the app. Over a focused
        // terminal they are cursor-back and kill-to-end-of-line, and the terminal wins: the reserved
        // set is deliberately small.
        using var pane = Pane.Focused();

        var claim = pane.Press(key, modifiers);

        Assert.Equal(expected, pane.Terminal.Text);
        Assert.Equal(KeyClaim.Command, claim);
        Assert.Empty(pane.App.Keys);
    }

    [Fact]
    public void CtrlC_InterruptsTheShellRatherThanCopying()
    {
        using var pane = Pane.Focused();

        var claim = pane.Press(KeyboardKey.C, InputModifiers.Control);

        Assert.Equal("\u0003", pane.Terminal.Text);
        Assert.Equal(KeyClaim.Command, claim);
    }

    // ---- when the pane should not have the keyboard ----

    [Fact]
    public void AHiddenPane_GivesTheKeyboardBack()
    {
        // The mode switcher keeps the pane alive and hides its view, so this is what "the user
        // switched to History" looks like from here.
        using var pane = Pane.Focused();
        pane.View.IsVisible = false;

        var claim = pane.Press(KeyboardKey.UpArrow);

        Assert.Empty(pane.Terminal.Written);
        Assert.Equal(KeyClaim.None, claim);
        Assert.NotSame(pane.Controller, pane.Harness.Input.FocusedComponent);
        Assert.Contains((KeyboardKey.UpArrow, InputModifiers.None), pane.App.Keys);
    }

    [Fact]
    public void WithNoShellYet_KeysFallThroughInsteadOfVanishing()
    {
        using var pane = Pane.Focused(TestTerminal.NotStarted());

        var claim = pane.Press(KeyboardKey.UpArrow);

        Assert.Empty(pane.Terminal.Written);
        Assert.Equal(KeyClaim.None, claim);
        Assert.Contains((KeyboardKey.UpArrow, InputModifiers.None), pane.App.Keys);
    }

    [Fact]
    public void WithTheKeyboardElsewhere_ThePaneTakesNothing()
    {
        using var pane = Pane.Create();
        pane.Hover();
        pane.Harness.Input.StealFocus(pane.App);

        var claim = pane.Press(KeyboardKey.UpArrow);

        Assert.Empty(pane.Terminal.Written);
        Assert.Equal(KeyClaim.None, claim);
    }

    [Fact]
    public void WithTheKeyboardElsewhere_TypedCharactersDoNotReachTheShell()
    {
        using var pane = Pane.Create();
        pane.Hover();
        pane.Harness.Input.StealFocus(pane.App);

        pane.Harness.SendText(new Rune('x'));

        Assert.Empty(pane.Terminal.Written);
    }

    static byte[] Output(string sequence) => Encoding.ASCII.GetBytes(sequence);

    /// <summary>
    /// A mounted terminal pane: the grid view, its input controller, a recording shell behind it,
    /// and a stand-in for the application's keybindings.
    /// </summary>
    sealed class Pane : IDisposable
    {
        Pane(
            GuiTestHarness harness,
            TerminalGridView view,
            TerminalInputController controller,
            AppKeybinds app,
            TestTerminal terminal)
        {
            Harness = harness;
            View = view;
            Controller = controller;
            App = app;
            Terminal = terminal;
        }

        public GuiTestHarness Harness { get; }
        public TerminalGridView View { get; }
        public TerminalInputController Controller { get; }
        public AppKeybinds App { get; }
        public TestTerminal Terminal { get; }

        public static Pane Create(TestTerminal? terminal = null)
        {
            var shell = terminal ?? TestTerminal.Live();
            var app = new AppKeybinds();
            TerminalGridView? view = null;
            TerminalInputController? controller = null;

            var harness = GuiTestHarness.Create(
                ctx =>
                {
                    var input = ctx.Require<InputSystem>();
                    view = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>());
                    controller = new TerminalInputController(view, input, shell);

                    // The app's keybindings live on the window root, an ancestor of the pane, so
                    // they sit earlier in the capture path than the terminal's own controller and
                    // see every key the terminal declines.
                    input.RegisterController(view, app);
                    input.RegisterController(view, controller);
                    return view;
                },
                configure: ctx =>
                {
                    ctx.AddService<IThemeService<ThemeStyles>>(
                        new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                    ctx.AddService<ILocalizationService>(
                        new LocalizationService(new State<Locale>(Locale.En)));
                });

            return new Pane(harness, view!, controller!, app, shell);
        }

        public static Pane Focused(TestTerminal? terminal = null)
        {
            var pane = Create(terminal);
            pane.Hover();
            pane.Harness.Input.StealFocus(pane.Controller);
            return pane;
        }

        /// <summary>Puts the pane in the dispatch path, which is what a mouse move over it does.</summary>
        public void Hover() => Harness.MoveTo(400f, 300f);

        public KeyClaim Press(KeyboardKey key, InputModifiers modifiers = InputModifiers.None) =>
            Send(key, InputState.Pressed, modifiers);

        public KeyClaim Release(KeyboardKey key, InputModifiers modifiers = InputModifiers.None) =>
            Send(key, InputState.Released, modifiers);

        /// <summary>
        /// Dispatches one key through the real input system and hands back how it was claimed. The
        /// harness's own <c>PressKey</c> discards the event, and command-versus-text-versus-unclaimed
        /// is the whole contract here: a command suppresses the character the key would produce, a
        /// text claim does not, and an unclaimed key is one the application still gets.
        /// </summary>
        KeyClaim Send(KeyboardKey key, InputState state, InputModifiers modifiers)
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

        public void Dispose()
        {
            Harness.Dispose();
            Terminal.Dispose();
        }
    }

    /// <summary>
    /// Stands in for the application's keybinding controller, recording what reached it.
    /// </summary>
    sealed class AppKeybinds : KeyboardMouseController
    {
        public List<(KeyboardKey Key, InputModifiers Modifiers)> Keys { get; } = [];

        public bool SawMousePress { get; private set; }

        public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
        {
            if (e.State == InputState.Pressed) Keys.Add((e.Key, e.Modifiers));
        }

        public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
        {
            if (e.State == InputState.Pressed) SawMousePress = true;
        }
    }
}

/// <summary>
/// The two members the pane's keyboard needs from its view model: whether there is a shell, and
/// where its bytes go.
/// </summary>
public class TerminalViewModelInputTests
{
    [Fact]
    public void BeforeAShellIsAdopted_TheViewModelIsNotAcceptingInput()
    {
        var dispatcher = new QueueDispatcher();
        using var vm = new TerminalViewModel(new RecordingLaunch(), dispatcher);

        Assert.False(vm.IsAcceptingInput);
    }

    [Fact]
    public void SendingInputWithNoShell_IsANoOpRatherThanAThrow()
    {
        var dispatcher = new QueueDispatcher();
        using var vm = new TerminalViewModel(new RecordingLaunch(), dispatcher);

        vm.SendInput("q"u8);
    }

    [Fact]
    public void OnceTheShellIsRunning_InputReachesIt()
    {
        var launch = new RecordingLaunch();
        using var vm = Started(launch, out _);

        vm.SendInput("q"u8);

        Assert.True(vm.IsAcceptingInput);
        Assert.Equal("q"u8.ToArray(), launch.Pty.Input);
    }

    [Fact]
    public void AfterDisposal_TheViewModelStopsAcceptingInput()
    {
        var launch = new RecordingLaunch();
        var vm = Started(launch, out _);

        vm.Dispose();

        Assert.False(vm.IsAcceptingInput);
    }

    /// <summary>
    /// A view model whose shell has started and been adopted. The start is a background task that
    /// posts its result, so the test waits for the post rather than for a duration.
    /// </summary>
    static TerminalViewModel Started(RecordingLaunch launch, out QueueDispatcher dispatcher)
    {
        dispatcher = new QueueDispatcher();
        var vm = new TerminalViewModel(launch, dispatcher);
        vm.ReportViewport(new TerminalSize(80, 24));

        Assert.True(dispatcher.WaitForPost(TimeSpan.FromSeconds(5)), "The shell never started.");
        dispatcher.Pump();
        return vm;
    }

    /// <summary>A launch over a pseudo-terminal that stays open, which is what "a shell is running"
    /// means. A RecordingPty with no output ends its stream on the first read, so the session would
    /// report its shell gone before the test had typed anything.</summary>
    sealed class RecordingLaunch : ITerminalLaunch
    {
        public SeamPty Pty { get; } = new();

        public TerminalSize SizeFor(TerminalSize viewport) => viewport;

        public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher) =>
            TerminalSession.Start(() => Pty, new XtermSharpEngineFactory(), size, dispatcher);
    }
}

/// <summary>
/// A shell for the keyboard to write to: a real <see cref="TerminalSession"/> over a pseudo-terminal
/// that records its input, so what a test asserts on is what the shell would have read.
/// </summary>
internal sealed class TestTerminal : ITerminalInput, IDisposable
{
    readonly RecordingPty _pty;
    readonly TerminalSession? _session;
    readonly int _baseline;

    TestTerminal(RecordingPty pty, TerminalSession? session)
    {
        _pty = pty;
        _session = session;

        // The engine answers some of what a program asks it, and those replies go up the terminal as
        // input. They are not keystrokes, so they are not what these tests are counting.
        _baseline = pty.Written.Length;
    }

    /// <summary>A running shell that has already produced <paramref name="output"/>.</summary>
    public static TestTerminal Live(byte[]? output = null)
    {
        var pty = new RecordingPty(output ?? []);
        var dispatcher = new QueueDispatcher();
        var session = TerminalSession.Start(
            () => pty,
            new XtermSharpEngineFactory(),
            new TerminalSize(80, 24),
            dispatcher);

        Assert.True(
            session.Exited.Wait(TimeSpan.FromSeconds(5)),
            "The recorded output never finished.");
        dispatcher.Pump();

        return new TestTerminal(pty, session);
    }

    /// <summary>A pane with no shell: either still starting, or failed to start.</summary>
    public static TestTerminal NotStarted() => new(new RecordingPty([]), null);

    public bool IsAcceptingInput => _session != null;

    public TerminalModes Modes => _session?.State.Modes ?? default;

    public void SendInput(ReadOnlySpan<byte> bytes) => _session?.Write(bytes);

    /// <summary>What the keyboard has sent to the shell.</summary>
    public byte[] Written => _pty.Written[_baseline..];

    public string Text => Encoding.Latin1.GetString(Written);

    public void Dispose() => _session?.Dispose();
}

/// <summary>
/// A pseudo-terminal that records everything written to it and replays a fixed script of output.
/// </summary>
internal sealed class RecordingPty : IPtySession
{
    readonly byte[] _output;
    readonly Lock _gate = new();
    readonly List<byte> _written = [];
    readonly TaskCompletionSource<PtyExit> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    int _offset;
    bool _disposed;

    public RecordingPty(byte[] output) => _output = output;

    public Task<PtyExit> Exited => _exited.Task;

    public byte[] Written
    {
        get
        {
            lock (_gate) return _written.ToArray();
        }
    }

    public int ReadOutput(Span<byte> buffer)
    {
        lock (_gate)
        {
            if (_disposed) return 0;

            var remaining = _output.Length - _offset;
            if (remaining <= 0)
            {
                _exited.TrySetResult(new PtyExit.Completed(0));
                return 0;
            }

            var take = Math.Min(remaining, buffer.Length);
            _output.AsSpan(_offset, take).CopyTo(buffer);
            _offset += take;
            return take;
        }
    }

    public void WriteInput(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate) _written.AddRange(bytes);
    }

    public void Resize(PtySize size) => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _exited.TrySetResult(new PtyExit.TornDown());
    }
}

/// <summary>
/// Collects posted work instead of running it, so a test says when the engine is fed rather than
/// racing the reader thread for it.
/// </summary>
internal sealed class QueueDispatcher : IUiDispatcher
{
    readonly ConcurrentQueue<Action> _queue = new();
    readonly SemaphoreSlim _posted = new(0);

    public void Post(Action action)
    {
        _queue.Enqueue(action);
        _posted.Release();
    }

    /// <summary>Waits for one posted action to arrive, for work that lands from another thread.</summary>
    public bool WaitForPost(TimeSpan timeout) => _posted.Wait(timeout);

    public void Pump()
    {
        while (_queue.TryDequeue(out var action))
            action();
    }
}
