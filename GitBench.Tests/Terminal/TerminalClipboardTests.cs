using System.Text;
using GitBench.Features.Terminal;
using GitBench.Localization;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// Copying and pasting: what the encoder puts on the wire, and which chords reach it.
/// </summary>
/// <remarks>
/// Escape characters are spelled as "\u001b" and never written literally, for the reason the rest of
/// this suite gives: an escape in a source literal is invisible in every diff that follows.
/// </remarks>
public class TerminalPasteEncoderTests
{
    const string Esc = "\u001b";

    [Fact]
    public void WithoutBracketedPaste_TheTextIsSentAsTyped()
    {
        Assert.Equal("hello", Encoded("hello", bracketed: false));
    }

    [Fact]
    public void WithBracketedPaste_TheTextIsWrappedSoTheProgramKnowsItWasPasted()
    {
        Assert.Equal($"{Esc}[200~hello{Esc}[201~", Encoded("hello", bracketed: true));
    }

    [Theory]
    [InlineData("a\r\nb")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void EveryLineEnding_BecomesTheCarriageReturnAKeyboardWouldHaveSent(string text)
    {
        Assert.Equal("a\rb", Encoded(text, bracketed: false));
    }

    [Fact]
    public void ACrLfPair_IsOneLineEndingRatherThanTwo()
    {
        Assert.Equal("a\rb\rc", Encoded("a\r\nb\r\nc", bracketed: false));
    }

    /// <remarks>
    /// The security property of bracketed paste. A clipboard carrying the closing sequence would
    /// otherwise end the bracket early and have everything after it read as typed input — which is
    /// the whole attack bracketed paste exists to prevent.
    /// </remarks>
    [Fact]
    public void ATerminatorInsideThePayload_IsStrippedRatherThanClosingTheBracketEarly()
    {
        var encoded = Encoded($"safe{Esc}[201~rm -rf /", bracketed: true);

        Assert.Equal($"{Esc}[200~saferm -rf /{Esc}[201~", encoded);
        Assert.Equal(1, Occurrences(encoded, $"{Esc}[201~"));
    }

    [Fact]
    public void WithoutBracketing_TheTerminatorIsLeftAlone()
    {
        // Nothing is bracketing it, so there is no bracket to break out of and no reason to edit
        // what the user copied.
        Assert.Equal($"a{Esc}[201~b", Encoded($"a{Esc}[201~b", bracketed: false));
    }

    [Fact]
    public void ANulByte_IsDroppedBecauseNoKeyboardCouldSendOne()
    {
        Assert.Equal("ab", Encoded("a\0b", bracketed: false));
    }

    [Fact]
    public void AnEmptyClipboard_EncodesToNothingAtAll()
    {
        Assert.Empty(TerminalPasteEncoder.Encode(string.Empty, bracketed: true));
    }

    [Fact]
    public void APasteLargerThanTheCap_IsTruncatedRatherThanSentWhole()
    {
        var huge = new string('x', TerminalPasteEncoder.MaxPastedCharacters + 500);

        var encoded = TerminalPasteEncoder.Encode(huge, bracketed: false);

        Assert.Equal(TerminalPasteEncoder.MaxPastedCharacters, encoded.Length);
    }

    /// <remarks>
    /// Multi-line paste with no bracketing runs every line but the last, which is what every
    /// terminal does and what this pane deliberately does too. Pinned so that changing it is a
    /// decision rather than an accident.
    /// </remarks>
    [Fact]
    public void AMultiLinePaste_IsSentAsIsWhenTheProgramHasNotAskedForBracketing()
    {
        Assert.Equal("one\rtwo\r", Encoded("one\ntwo\n", bracketed: false));
    }

    static string Encoded(string text, bool bracketed) =>
        Encoding.UTF8.GetString(TerminalPasteEncoder.Encode(text, bracketed));

    static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}

/// <summary>
/// What a program sends through OSC 52 before it reaches the system clipboard.
/// </summary>
public class ClipboardTextTests
{
    [Fact]
    public void TextTheUserHighlighted_GoesThroughUntouched()
    {
        Assert.Equal("a\u0007b", ClipboardText.FromSelection("a\u0007b").Value);
    }

    [Fact]
    public void TabsAndNewlines_SurviveAProgramsCopy()
    {
        Assert.Equal("a\tb\nc", ClipboardText.FromProgram("a\tb\nc")?.Value);
    }

    /// <remarks>
    /// The reason this type exists. A clipboard is pasted into other terminals, so a carriage return
    /// that hides what follows it, or an escape sequence, is not something a program gets to stage
    /// there on the user's behalf.
    /// </remarks>
    [Theory]
    [InlineData("git push\r--force", "git push--force")]
    [InlineData("a\u001b[31mb", "a[31mb")]
    [InlineData("a\0b", "ab")]
    [InlineData("a\u0007b", "ab")]
    public void ControlCharacters_AreStrippedFromWhatAProgramCopies(string sent, string expected)
    {
        Assert.Equal(expected, ClipboardText.FromProgram(sent)?.Value);
    }

    [Fact]
    public void APayloadOfNothingButControlCharacters_IsNotACopyAtAll()
    {
        Assert.Null(ClipboardText.FromProgram("\u0007\0"));
    }

    [Fact]
    public void AnEmptyPayload_IsNotACopyAtAll()
    {
        Assert.Null(ClipboardText.FromProgram(string.Empty));
    }

    [Fact]
    public void ACopyLargerThanTheCap_IsTruncated()
    {
        var huge = new string('x', ClipboardText.MaxCharacters + 100);

        Assert.Equal(ClipboardText.MaxCharacters, ClipboardText.FromProgram(huge)?.Value.Length);
    }
}

/// <summary>
/// The clipboard chords, dispatched through the real input system.
/// </summary>
public class TerminalClipboardChordTests
{
    static InputModifiers Clipboard =>
        OperatingSystem.IsMacOS()
            ? InputModifiers.Super
            : InputModifiers.Control | InputModifiers.Shift;

    [Fact]
    public void TheCopyChord_PutsTheSelectionOnTheClipboard()
    {
        using var pane = ClipboardPane.Focused("hello world");
        pane.Terminal.Select(new GridPoint(0, 0), new GridPoint(4, 0), SelectionGranularity.Character);

        pane.Press(KeyboardKey.C, Clipboard);

        Assert.Equal("hello", pane.Clipboard.Text);
    }

    [Fact]
    public void TheCopyChord_IsClaimedEvenWithNothingSelected_SoItAlwaysMeansOneThing()
    {
        using var pane = ClipboardPane.Focused("hello");

        var claim = pane.Press(KeyboardKey.C, Clipboard);

        Assert.Equal(KeyClaim.Command, claim);
        Assert.Null(pane.Clipboard.Text);
    }

    /// <remarks>
    /// The chord this whole scheme is arranged around. A terminal that swallowed Ctrl+C would be
    /// broken, so copy carries Shift on the platforms where Ctrl is the interrupt.
    /// </remarks>
    [Fact]
    public void CtrlC_IsStillTheInterruptRatherThanACopy()
    {
        using var pane = ClipboardPane.Focused("hello");
        pane.Terminal.Select(new GridPoint(0, 0), new GridPoint(4, 0), SelectionGranularity.Character);

        pane.Press(KeyboardKey.C, InputModifiers.Control);

        Assert.Equal("\u0003", pane.Terminal.Text);
        Assert.Null(pane.Clipboard.Text);
    }

    [Fact]
    public void ThePasteChord_SendsTheClipboardToTheShell()
    {
        using var pane = ClipboardPane.Focused("prompt");
        pane.Clipboard.Text = "pasted";

        pane.Press(KeyboardKey.V, Clipboard);

        Assert.Equal("pasted", pane.Terminal.Text);
    }

    [Fact]
    public void ThePasteChord_IsClaimedAsACommand_SoTheKeyDoesNotAlsoTypeItsCharacter()
    {
        using var pane = ClipboardPane.Focused("prompt");
        pane.Clipboard.Text = "x";

        var claim = pane.Press(KeyboardKey.V, Clipboard);

        Assert.Equal(KeyClaim.Command, claim);
        Assert.Equal("x", pane.Terminal.Text);
    }

    [Fact]
    public void PastingIntoAProgramThatAskedForBracketing_WrapsIt()
    {
        using var pane = ClipboardPane.Focused("prompt", bracketedPaste: true);
        pane.Clipboard.Text = "x";

        pane.Press(KeyboardKey.V, Clipboard);

        Assert.Equal("\u001b[200~x\u001b[201~", pane.Terminal.Text);
    }

    [Fact]
    public void AnEmptyClipboard_SendsNothingRatherThanAnEmptyBracket()
    {
        using var pane = ClipboardPane.Focused("prompt", bracketedPaste: true);

        pane.Press(KeyboardKey.V, Clipboard);

        Assert.Empty(pane.Terminal.Written);
    }
}

/// <summary>A clipboard that stays in the test rather than on the machine running it.</summary>
internal sealed class FakeClipboard : IClipboard
{
    public string? Text { get; set; }

    public void SetText(string text) => Text = text;

    public string? GetText() => Text;
}

/// <summary>
/// A mounted pane whose controller has a clipboard, which the shared harness deliberately does not
/// give it.
/// </summary>
internal sealed class ClipboardPane : IDisposable
{
    ClipboardPane(
        GuiTestHarness harness,
        TerminalInputController controller,
        TestTerminal terminal,
        FakeClipboard clipboard)
    {
        Harness = harness;
        Controller = controller;
        Terminal = terminal;
        Clipboard = clipboard;
    }

    public GuiTestHarness Harness { get; }
    public TerminalInputController Controller { get; }
    public TestTerminal Terminal { get; }
    public FakeClipboard Clipboard { get; }

    public static ClipboardPane Focused(string screen, bool bracketedPaste = false)
    {
        var output = bracketedPaste ? "\u001b[?2004h" + screen : screen;
        var shell = TestTerminal.Live(Encoding.UTF8.GetBytes(output));
        var clipboard = new FakeClipboard();
        TerminalInputController? controller = null;

        var harness = GuiTestHarness.Create(
            ctx =>
            {
                var input = ctx.Require<InputSystem>();
                var view = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>());
                controller = new TerminalInputController(view, input, shell, view, clipboard);
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

        var pane = new ClipboardPane(harness, controller!, shell, clipboard);
        harness.MoveTo(400f, 300f);
        harness.Input.StealFocus(controller!);
        return pane;
    }

    public KeyClaim Press(KeyboardKey key, InputModifiers modifiers)
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

    public void Dispose()
    {
        Harness.Dispose();
        Terminal.Dispose();
    }
}
