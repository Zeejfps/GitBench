using System.Collections.Concurrent;
using System.Text;
using GitBench.Features.Terminal;
using GitBench.Localization;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using ZGF.Geometry;
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

    // ---- the wheel and the page keys, which move the pane and not the shell ----

    [Fact]
    public void TheWheel_ScrollsTheHistoryWithoutTheKeyboard()
    {
        // Hover, not focus: the wheel belongs to whatever is under the pointer, which is how the
        // rest of the window behaves.
        using var pane = Pane.Create(TestTerminal.Live(Lines(50)));
        pane.Hover();

        pane.Harness.Scroll(0f, 1f);

        Assert.Equal(3, pane.Terminal.ScrollOffset);
        Assert.Empty(pane.Terminal.Written);
    }

    [Fact]
    public void TheWheelTheOtherWay_ComesBackTowardsTheShell()
    {
        using var pane = Pane.Create(TestTerminal.Live(Lines(50)));
        pane.Hover();
        pane.Harness.Scroll(0f, 2f);

        pane.Harness.Scroll(0f, -1f);

        Assert.Equal(3, pane.Terminal.ScrollOffset);
    }

    [Fact]
    public void AGestureOfFractionsOfALine_IsNotRoundedAway()
    {
        // A trackpad reports one swipe as a stream of small deltas. Truncating each on its own would
        // throw the whole gesture away and the pane would sit still under a moving finger.
        using var pane = Pane.Create(TestTerminal.Live(Lines(50)));
        pane.Hover();

        for (var event_ = 0; event_ < 5; event_++)
            pane.Harness.Scroll(0f, 0.2f);

        Assert.Equal(3, pane.Terminal.ScrollOffset);
    }

    [Fact]
    public void AWheelOverAScreenWithNoHistory_IsLeftToWhateverScrollsBehindIt()
    {
        using var pane = Pane.Create();
        pane.Hover();

        pane.Harness.Scroll(0f, 1f);

        Assert.True(pane.App.SawWheel, "The pane swallowed a wheel event it did nothing with.");
    }

    [Fact]
    public void TheWheelOverSomewhereElse_IsNotThePanesToTake()
    {
        using var pane = Pane.Focused(TestTerminal.Live(Lines(50)));
        pane.Harness.MoveTo(-50f, -50f);

        pane.Harness.Scroll(0f, 1f);

        Assert.Equal(0, pane.Terminal.ScrollOffset);
    }

    // ---- the wheel over a program that reads the mouse itself ----

    [Fact]
    public void TheWheel_OverAProgramReadingTheMouse_IsAReportAndNotTheHistory()
    {
        using var pane = Pane.Create(TestTerminal.Live(Then(Lines(50), Tracking)), Cells(4, 2));
        pane.Hover();

        pane.Harness.Scroll(0f, 1f);

        Assert.Equal(
            Csi + "<64;5;3M" + Csi + "<64;5;3M" + Csi + "<64;5;3M",
            pane.Terminal.Text);
        Assert.Equal(0, pane.Terminal.ScrollOffset);
    }

    [Fact]
    public void TheWheelTheOtherWay_OverAProgramReadingTheMouse_IsButtonSixtyFive()
    {
        using var pane = Pane.Create(TestTerminal.Live(Tracking), Cells(0, 0));
        pane.Hover();

        pane.Harness.Scroll(0f, -1f / 3f);

        Assert.Equal(Csi + "<65;1;1M", pane.Terminal.Text);
    }

    [Fact]
    public void TheWheel_OverAFullScreenProgram_BecomesTheCursorKeysItAlreadyReads()
    {
        using var pane = Pane.Create(TestTerminal.Live(Then(Lines(50), AltScreen)), Cells(0, 0));
        pane.Hover();

        pane.Harness.Scroll(0f, 1f);

        Assert.Equal(Csi + "A" + Csi + "A" + Csi + "A", pane.Terminal.Text);
    }

    [Fact]
    public void TheWheelTheOtherWay_OverAFullScreenProgram_IsTheDownArrow()
    {
        using var pane = Pane.Create(TestTerminal.Live(AltScreen), Cells(0, 0));
        pane.Hover();

        pane.Harness.Scroll(0f, -1f / 3f);

        Assert.Equal(Csi + "B", pane.Terminal.Text);
    }

    [Fact]
    public void AProgramThatTurnsAlternateScrollOff_GetsNoWheelAtAll()
    {
        using var pane = Pane.Create(
            TestTerminal.Live(Then(AltScreen, Output(Csi + "?1007l"))),
            Cells(0, 0));
        pane.Hover();

        pane.Harness.Scroll(0f, 1f);

        Assert.Empty(pane.Terminal.Written);
        Assert.True(pane.App.SawWheel, "The pane swallowed a wheel event it did nothing with.");
    }

    [Fact]
    public void TheWheelOverTheNormalScreen_IsStillThePanesOwnHistory()
    {
        using var pane = Pane.Create(TestTerminal.Live(Lines(50)), Cells(0, 0));
        pane.Hover();

        pane.Harness.Scroll(0f, 1f);

        Assert.Equal(3, pane.Terminal.ScrollOffset);
        Assert.Empty(pane.Terminal.Written);
    }

    // ---- Shift, which takes the wheel back from whatever is reading it ----

    [Fact]
    public void ShiftWithTheWheel_OverAProgramReadingTheMouse_IsThePanesOwnHistory()
    {
        // The reason the wheel needed modifiers at all: a program tracking the mouse otherwise owns
        // the wheel outright, and the history behind it could not be read without quitting it.
        using var pane = Pane.Create(TestTerminal.Live(Then(Lines(50), Tracking)), Cells(4, 2));
        pane.Hover();

        pane.Harness.Scroll(0f, 1f, InputModifiers.Shift);

        Assert.Equal(3, pane.Terminal.ScrollOffset);
        Assert.Empty(pane.Terminal.Written);
    }

    [Fact]
    public void ShiftWithTheWheel_OverAFullScreenProgram_IsNotTheCursorKeys()
    {
        // The alternate screen has no history of its own, so there is nothing for Shift to show —
        // but it must still not be answered with the arrows the program would read as movement.
        using var pane = Pane.Create(TestTerminal.Live(Then(Lines(50), AltScreen)), Cells(0, 0));
        pane.Hover();

        pane.Harness.Scroll(0f, 1f, InputModifiers.Shift);

        Assert.Empty(pane.Terminal.Written);
    }

    [Fact]
    public void ShiftWithTheWheel_OverTheNormalScreen_ScrollsItLikeTheBareWheel()
    {
        using var pane = Pane.Create(TestTerminal.Live(Lines(50)), Cells(0, 0));
        pane.Hover();

        pane.Harness.Scroll(0f, 1f, InputModifiers.Shift);

        Assert.Equal(3, pane.Terminal.ScrollOffset);
    }

    [Fact]
    public void ControlWithTheWheel_OverAProgramReadingTheMouse_CarriesTheModifier()
    {
        // Ctrl is not taken back — it rides along in the report, which is how a program that zooms
        // on Ctrl+wheel hears about it. 64 is wheel-up, 16 is the control bit.
        using var pane = Pane.Create(TestTerminal.Live(Tracking), Cells(0, 0));
        pane.Hover();

        pane.Harness.Scroll(0f, 1f / 3f, InputModifiers.Control);

        Assert.Equal(Csi + "<80;1;1M", pane.Terminal.Text);
    }

    // ---- how far one turn of the wheel goes ----

    [Fact]
    public void ATrackpad_MovesFewerLinesThanAWheelReportingTheSameDelta()
    {
        using var wheel = Pane.Create(TestTerminal.Live(Lines(50)), Cells(0, 0));
        using var trackpad = Pane.Create(TestTerminal.Live(Lines(50)), Cells(0, 0));
        wheel.Hover();
        trackpad.Hover();

        wheel.Harness.Scroll(0f, 1f);
        trackpad.Harness.Scroll(0f, 1f, gesture: ScrollPhase.Changed);

        Assert.Equal(3, wheel.Terminal.ScrollOffset);
        Assert.Equal(1, trackpad.Terminal.ScrollOffset);
    }

    [Fact]
    public void TheMomentumAfterAFlick_IsScaledLikeTheFlickAndNotLikeAWheel()
    {
        // Momentum arrives with no gesture phase — the fingers have already lifted — so it has to be
        // recognised on its own or the tail of every flick would accelerate.
        using var pane = Pane.Create(TestTerminal.Live(Lines(50)), Cells(0, 0));
        pane.Hover();

        pane.Harness.Scroll(0f, 1f, momentum: ScrollPhase.Changed);

        Assert.Equal(1, pane.Terminal.ScrollOffset);
    }

    // ---- clicks and the pointer ----

    [Fact]
    public void AClick_OverAProgramReadingTheMouse_IsReportedAndKeptFromTheApplication()
    {
        using var pane = Pane.Create(TestTerminal.Live(Tracking), Cells(7, 1));

        pane.Harness.Click(400f, 300f);

        Assert.Equal(Csi + "<0;8;2M" + Csi + "<0;8;2m", pane.Terminal.Text);
        Assert.False(pane.App.SawMousePress, "The program's click also reached the rest of the pane.");
    }

    [Fact]
    public void AClickOverAProgramReadingTheMouse_StillTakesTheKeyboard()
    {
        using var pane = Pane.Create(TestTerminal.Live(Tracking), Cells(0, 0));

        pane.Harness.Click(400f, 300f);

        Assert.Same(pane.Controller, pane.Harness.Input.FocusedComponent);
    }

    [Fact]
    public void ThePointerMoving_IsReportedOncePerCell()
    {
        var cells = Cells(3, 3);
        using var pane = Pane.Create(TestTerminal.Live(AnyEventTracking), cells);

        pane.Harness.MoveTo(400f, 300f);
        pane.Harness.MoveTo(401f, 300f);
        cells.At(4, 3);
        pane.Harness.MoveTo(410f, 300f);

        Assert.Equal(Csi + "<35;4;4M" + Csi + "<35;5;4M", pane.Terminal.Text);
    }

    [Fact]
    public void ThePointerMoving_OverAProgramThatNeverAskedForIt_IsNotReported()
    {
        using var pane = Pane.Create(TestTerminal.Live(Tracking), Cells(3, 3));

        pane.Harness.MoveTo(400f, 300f);
        pane.Harness.MoveTo(500f, 200f);

        Assert.Empty(pane.Terminal.Written);
    }

    [Theory]
    [InlineData(KeyboardKey.PageUp, 23)]
    [InlineData(KeyboardKey.PageDown, 0)]
    public void ShiftWithAPageKey_MovesTheHistoryByAScreenInsteadOfReachingTheShell(
        KeyboardKey key,
        int expected)
    {
        // Twenty-four rows, so a page is twenty-three and the reader keeps one line of overlap.
        using var pane = Pane.Focused(TestTerminal.Live(Lines(50)));

        var claim = pane.Press(key, InputModifiers.Shift);

        Assert.Equal(expected, pane.Terminal.ScrollOffset);
        Assert.Empty(pane.Terminal.Written);
        Assert.Equal(KeyClaim.Command, claim);
    }

    [Fact]
    public void ShiftPageUpAtTheTopOfTheHistory_IsStillNotTheShells()
    {
        // Consumed whether or not it moved. Falling through at the top would send the shell a
        // sequence the user has spent the last four presses not sending it.
        using var pane = Pane.Focused(TestTerminal.Live(Lines(50)));
        pane.Press(KeyboardKey.PageUp, InputModifiers.Shift);
        pane.Press(KeyboardKey.PageUp, InputModifiers.Shift);

        var claim = pane.Press(KeyboardKey.PageUp, InputModifiers.Shift);

        Assert.Empty(pane.Terminal.Written);
        Assert.Equal(KeyClaim.Command, claim);
    }

    [Fact]
    public void APageKeyWithoutShift_IsStillTheShells()
    {
        using var pane = Pane.Focused(TestTerminal.Live(Lines(50)));

        pane.Press(KeyboardKey.PageUp);

        Assert.Equal(Csi + "5~", pane.Terminal.Text);
        Assert.Equal(0, pane.Terminal.ScrollOffset);
    }

    [Fact]
    public void ScrollingBackWithNoShellLeft_StillWorks()
    {
        // The history outlives the process that printed it, and reading back through what a command
        // printed is most wanted once it has finished printing it.
        using var pane = Pane.Create(TestTerminal.Exited(Lines(50)));
        pane.Hover();
        pane.Harness.Input.StealFocus(pane.Controller);

        pane.Harness.Scroll(0f, 1f);
        pane.Press(KeyboardKey.PageUp, InputModifiers.Shift);

        Assert.Equal(3 + 23, pane.Terminal.ScrollOffset);
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

    /// <summary>Button-event tracking with SGR reports: what a full-screen program turns on.</summary>
    static byte[] Tracking => Output(Csi + "?1002h" + Csi + "?1006h");

    static byte[] AnyEventTracking => Output(Csi + "?1003h" + Csi + "?1006h");

    static byte[] AltScreen => Output(Csi + "?1049h");

    static byte[] Then(byte[] first, byte[] second) => [.. first, .. second];

    static FixedCells Cells(int column, int row) => new(column, row);

    /// <summary>Numbered lines, enough of them to leave a history behind a twenty-four-row screen.</summary>
    static byte[] Lines(int count) =>
        Encoding.ASCII.GetBytes(string.Join("\r\n", Enumerable.Range(0, count).Select(line => $"l{line}")));

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

        public static Pane Create(
            TestTerminal? terminal = null,
            ITerminalCellGeometry? cells = null)
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
                    controller = new TerminalInputController(view, input, shell, cells ?? view);

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

        /// <summary>
        /// Only what bubbled. Every event passes this controller on the way down as well, so
        /// recording the capture pass would say "the pane declined it" about every event there is.
        /// </summary>
        public bool SawWheel { get; private set; }

        public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
        {
            if (e.State == InputState.Pressed) Keys.Add((e.Key, e.Modifiers));
        }

        public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
        {
            if (e.Phase == EventPhase.Bubbling && e.State == InputState.Pressed) SawMousePress = true;
        }

        public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
        {
            if (e.Phase == EventPhase.Bubbling) SawWheel = true;
        }
    }
}

/// <summary>
/// The two members the pane's keyboard needs from its view model: whether there is a shell, and
/// where its bytes go.
/// </summary>
public class TerminalInstanceInputTests
{
    [Fact]
    public void BeforeAShellIsAdopted_TheTerminalIsNotAcceptingInput()
    {
        var dispatcher = new QueueDispatcher();
        using var vm = new TerminalInstance(new RecordingLaunch(), dispatcher);

        Assert.False(vm.IsAcceptingInput);
    }

    [Fact]
    public void SendingInputWithNoShell_IsANoOpRatherThanAThrow()
    {
        var dispatcher = new QueueDispatcher();
        using var vm = new TerminalInstance(new RecordingLaunch(), dispatcher);

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
    public void TypingWhileScrolledBack_ReturnsToTheLiveScreen()
    {
        // A keystroke the sender cannot see land is worse than losing their place in the history,
        // so the pane comes back to the prompt on its own rather than the controller remembering to
        // ask it to.
        var launch = new RecordingLaunch();
        using var vm = Started(launch, out var dispatcher);
        Print(launch, dispatcher, 50);
        Assert.True(vm.Scroll(5), "There was no history to scroll back through.");

        vm.SendInput("q"u8);

        Assert.False(vm.Scroll(-1), "The pane was still somewhere back in the history.");
    }

    [Fact]
    public void AProgramsOwnReplies_DoNotMoveTheReadersPlaceInTheHistory()
    {
        // The engine answers a program's questions up the same terminal, and a program asking what
        // size its terminal is must not yank the screen out from under whoever is reading it.
        var launch = new RecordingLaunch();
        using var vm = Started(launch, out var dispatcher);
        Print(launch, dispatcher, 50);
        vm.Scroll(5);

        // DSR: the engine replies with the cursor position, through the session and not through
        // the seam the keyboard uses.
        Emit(launch, dispatcher, "\u001b[6n");

        Assert.True(vm.Scroll(-1), "The reply moved the pane back to the live screen.");
    }

    [Fact]
    public void AfterDisposal_TheTerminalStopsAcceptingInput()
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
    static TerminalInstance Started(RecordingLaunch launch, out QueueDispatcher dispatcher)
    {
        dispatcher = new QueueDispatcher();
        var vm = new TerminalInstance(launch, dispatcher);
        vm.ReportViewport(new TerminalSize(80, 24));
        vm.Start();

        Assert.True(dispatcher.WaitForPost(TimeSpan.FromSeconds(5)), "The shell never started.");
        dispatcher.Pump();
        return vm;
    }

    /// <summary>Prints numbered lines into the shell's terminal and lets the engine take them.</summary>
    static void Print(RecordingLaunch launch, QueueDispatcher dispatcher, int lines) =>
        Emit(
            launch,
            dispatcher,
            string.Join("\r\n", Enumerable.Range(0, lines).Select(line => $"l{line}")));

    static void Emit(RecordingLaunch launch, QueueDispatcher dispatcher, string output)
    {
        launch.Pty.Emit(output);
        Assert.True(dispatcher.WaitForPost(TimeSpan.FromSeconds(5)), "The output never arrived.");
        dispatcher.Pump();
    }

    /// <summary>A launch over a pseudo-terminal that stays open, which is what "a shell is running"
    /// means. A RecordingPty with no output ends its stream on the first read, so the session would
    /// report its shell gone before the test had typed anything.</summary>
    sealed class RecordingLaunch : ITerminalLaunch
    {
        TerminalSession? _session;

        public SeamPty RawPty { get; } = new();

        /// <remarks>
        /// The session queues input for its writer thread, so what reached the terminal is only
        /// everything written once that queue is empty.
        /// </remarks>
        public SeamPty Pty
        {
            get
            {
                _session?.Flush(TimeSpan.FromSeconds(5));
                return RawPty;
            }
        }

        public string Name => "shell";

        public TerminalSize SizeFor(TerminalSize viewport) => viewport;

        public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher) =>
            _session = TerminalSession.Start(() => RawPty, new XtermSharpEngineFactory(), size, dispatcher);
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

    bool _exited;

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

    /// <summary>A shell that has printed <paramref name="output"/> and then gone, leaving its screen
    /// and its history behind — which is still somewhere a reader wants to scroll.</summary>
    public static TestTerminal Exited(byte[] output)
    {
        var terminal = Live(output);
        terminal._exited = true;
        return terminal;
    }

    public bool IsAcceptingInput => _session != null && !_exited;

    public TerminalModes Modes => _session?.State.Modes ?? default;

    public void SendMouse(ReadOnlySpan<byte> bytes) => _session?.Write(bytes);

    public void SendInput(ReadOnlySpan<byte> bytes) => _session?.Write(bytes);

    public bool Scroll(int lines) => _session?.Scroll(lines) ?? false;

    public bool ScrollPages(int pages) => _session?.ScrollPages(pages) ?? false;

    public void Paste(string text)
    {
        if (_session is not { } session) return;

        var bytes = TerminalPasteEncoder.Encode(text, session.State.Modes.BracketedPaste);
        if (bytes.Length > 0) session.Write(bytes);
    }

    public bool HasScreen => _session != null;

    public TerminalSpan? Selection => _session?.Selection;

    public bool Select(GridPoint anchor, GridPoint focus, SelectionGranularity granularity) =>
        _session?.Select(anchor, focus, granularity) ?? false;

    public bool SelectAll() => _session?.SelectAll() ?? false;

    public bool ClearSelection() => _session?.ClearSelection() ?? false;

    public string SelectionText() => _session?.SelectionText() ?? string.Empty;

    /// <summary>Where the viewport is, so a wheel test can assert on the pane and not on the wheel.</summary>
    public int ScrollOffset => _session?.ScrollOffset ?? 0;

    /// <summary>What the keyboard has sent to the shell.</summary>
    /// <remarks>
    /// Drained first: the session queues input for its writer thread, so what has reached the
    /// terminal is not everything written until the queue is empty.
    /// </remarks>
    public byte[] Written
    {
        get
        {
            _session?.Flush(TimeSpan.FromSeconds(5));
            return _pty.Written[_baseline..];
        }
    }

    public string Text => Encoding.Latin1.GetString(Written);

    public void Dispose() => _session?.Dispose();
}

/// <summary>
/// A pane whose cells are wherever the test says they are, so a mouse report can be asserted
/// without rendering one.
/// </summary>
internal sealed class FixedCells(int column, int row) : ITerminalCellGeometry
{
    int _column = column;
    int _row = row;

    public void At(int column, int row)
    {
        _column = column;
        _row = row;
    }

    public bool TryLocate(PointF point, out int column, out int row)
    {
        column = _column;
        row = _row;
        return true;
    }

    public GridPoint? ClampToGrid(PointF point) => new GridPoint(_column, _row);

    public int Redraws { get; private set; }

    public void RequestRedraw() => Redraws++;

    public TerminalLinkTarget? LinkAt(PointF point) => Link;

    /// <summary>The link every point of this pane is over, or null for none.</summary>
    public TerminalLinkTarget? Link { get; set; }

    public PointF? HoverPoint { get; private set; }

    public void SetHoverPoint(PointF? point) => HoverPoint = point;

    public TerminalLinkTarget? HoveredLink => HoverPoint is null ? null : Link;
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
