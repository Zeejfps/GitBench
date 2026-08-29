using System.Text;
using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The key encoder's table: what byte sequence each key press becomes on the wire, and which keys
/// deliberately become nothing because the text pipeline will carry them instead.
/// </summary>
/// <remarks>
/// Every expectation is spelled with <c>"\u001b"</c> rather than a literal escape character: an
/// escape in a source literal is invisible in every diff and review that follows. Control bytes are
/// written the same way, so an expectation reads as the bytes it is.
/// </remarks>
public class TerminalKeyEncoderTests
{
    const string Esc = "\u001b";

    // ---- keys the text pipeline delivers ----

    [Theory]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.Shift)]
    [InlineData(TerminalKey.Z, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.Z, TerminalKeyModifiers.Shift)]
    [InlineData(TerminalKey.Space, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.Space, TerminalKeyModifiers.Shift)]
    public void AKeyTheTextPipelineWillDeliver_EncodesToNothing(
        TerminalKey key,
        TerminalKeyModifiers modifiers)
    {
        // The seam the whole design rests on: with nothing to send, the controller can claim the key
        // as text and let the OS decide what character a layout, a dead key or an IME makes of it.
        Assert.Equal(0, Length(key, modifiers));
    }

    // ---- control keys ----

    [Theory]
    [InlineData(TerminalKey.Enter, "\r")]
    [InlineData(TerminalKey.Backspace, "\u007f")]
    [InlineData(TerminalKey.Tab, "\t")]
    [InlineData(TerminalKey.Escape, Esc)]
    public void AControlKey_EncodesToItsSingleByte(TerminalKey key, string expected) =>
        Assert.Equal(expected, Encoded(key));

    [Fact]
    public void Enter_IsCarriageReturn_UnderEveryModifierThatIsNotAlt()
    {
        // Legacy encoding has nothing better to say about Shift+Enter or Ctrl+Enter, and inventing
        // a line feed for either breaks every shell that reads a line.
        Assert.Equal("\r", Encoded(TerminalKey.Enter));
        Assert.Equal("\r", Encoded(TerminalKey.Enter, TerminalKeyModifiers.Shift));
        Assert.Equal("\r", Encoded(TerminalKey.Enter, TerminalKeyModifiers.Ctrl));
    }

    // ---- cursor keys and Home/End ----

    [Theory]
    [InlineData(TerminalKey.Up, "A")]
    [InlineData(TerminalKey.Down, "B")]
    [InlineData(TerminalKey.Right, "C")]
    [InlineData(TerminalKey.Left, "D")]
    [InlineData(TerminalKey.Home, "H")]
    [InlineData(TerminalKey.End, "F")]
    public void ACursorKey_IsACsiSequence_WhileTheProgramWantsNormalCursorKeys(
        TerminalKey key,
        string final) =>
        Assert.Equal(Csi(final), Encoded(key));

    [Theory]
    [InlineData(TerminalKey.Up, "A")]
    [InlineData(TerminalKey.Down, "B")]
    [InlineData(TerminalKey.Right, "C")]
    [InlineData(TerminalKey.Left, "D")]
    [InlineData(TerminalKey.Home, "H")]
    [InlineData(TerminalKey.End, "F")]
    public void ACursorKey_IsAnSs3Sequence_OnceTheProgramHasAskedForApplicationCursorKeys(
        TerminalKey key,
        string final) =>
        Assert.Equal(Ss3(final), Encoded(key, applicationCursorKeys: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AModifiedCursorKey_IsCsi_WhicheverCursorKeyModeIsSet(bool applicationCursorKeys) =>
        Assert.Equal(
            Csi("1;2A"),
            Encoded(TerminalKey.Up, TerminalKeyModifiers.Shift, applicationCursorKeys));

    // ---- the modifier parameter ----

    [Theory]
    [InlineData(TerminalKeyModifiers.Shift, "1;2C")]
    [InlineData(TerminalKeyModifiers.Alt, "1;3C")]
    [InlineData(TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt, "1;4C")]
    [InlineData(TerminalKeyModifiers.Ctrl, "1;5C")]
    [InlineData(TerminalKeyModifiers.Ctrl | TerminalKeyModifiers.Shift, "1;6C")]
    [InlineData(TerminalKeyModifiers.Ctrl | TerminalKeyModifiers.Alt, "1;7C")]
    [InlineData(
        TerminalKeyModifiers.Ctrl | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Shift,
        "1;8C")]
    public void TheModifierParameter_IsTheXtermBitsPlusOne(
        TerminalKeyModifiers modifiers,
        string expected) =>
        Assert.Equal(Csi(expected), Encoded(TerminalKey.Right, modifiers));

    [Fact]
    public void AFullyModifiedCursorKey_KeepsItsOwnFinalByte() =>
        Assert.Equal(
            Csi("1;8D"),
            Encoded(
                TerminalKey.Left,
                TerminalKeyModifiers.Ctrl | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Shift));

    // ---- editing keys ----

    [Theory]
    [InlineData(TerminalKey.Insert, "2~")]
    [InlineData(TerminalKey.Delete, "3~")]
    [InlineData(TerminalKey.PageUp, "5~")]
    [InlineData(TerminalKey.PageDown, "6~")]
    public void AnEditingKey_IsItsNumberedTildeSequence(TerminalKey key, string expected) =>
        Assert.Equal(Csi(expected), Encoded(key));

    [Theory]
    [InlineData(TerminalKey.Delete, TerminalKeyModifiers.Ctrl, "3;5~")]
    [InlineData(TerminalKey.PageDown, TerminalKeyModifiers.Shift, "6;2~")]
    public void AModifiedEditingKey_CarriesTheModifierAsASecondParameter(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected) =>
        Assert.Equal(Csi(expected), Encoded(key, modifiers));

    // ---- function keys ----

    [Theory]
    [InlineData(TerminalKey.F1, "P")]
    [InlineData(TerminalKey.F2, "Q")]
    [InlineData(TerminalKey.F3, "R")]
    [InlineData(TerminalKey.F4, "S")]
    public void TheFirstFourFunctionKeys_AreSs3Sequences(TerminalKey key, string final) =>
        Assert.Equal(Ss3(final), Encoded(key));

    [Theory]
    [InlineData(TerminalKey.F5, "15~")]
    [InlineData(TerminalKey.F6, "17~")]
    [InlineData(TerminalKey.F7, "18~")]
    [InlineData(TerminalKey.F8, "19~")]
    [InlineData(TerminalKey.F9, "20~")]
    [InlineData(TerminalKey.F10, "21~")]
    [InlineData(TerminalKey.F11, "23~")]
    [InlineData(TerminalKey.F12, "24~")]
    public void TheRestOfTheFunctionKeys_AreNumberedTildeSequences_WithTheHistoricalGaps(
        TerminalKey key,
        string expected)
    {
        // 16 and 22 are skipped. Numbering these consecutively still passes an F5-only test and then
        // sends F11 as F10 to everything the user runs.
        Assert.Equal(Csi(expected), Encoded(key));
    }

    [Theory]
    [InlineData(TerminalKey.F1, TerminalKeyModifiers.Ctrl, "1;5P")]
    [InlineData(TerminalKey.F4, TerminalKeyModifiers.Shift, "1;2S")]
    public void AModifiedLowFunctionKey_BecomesCsiAndKeepsItsFinalByte(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected) =>
        Assert.Equal(Csi(expected), Encoded(key, modifiers));

    [Theory]
    [InlineData(TerminalKey.F5, TerminalKeyModifiers.Ctrl, "15;5~")]
    [InlineData(TerminalKey.F12, TerminalKeyModifiers.Shift, "24;2~")]
    public void AModifiedHighFunctionKey_CarriesTheModifierAsASecondParameter(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected) =>
        Assert.Equal(Csi(expected), Encoded(key, modifiers));

    // ---- control chords ----

    [Theory]
    [InlineData(TerminalKey.A, "\u0001")]
    [InlineData(TerminalKey.M, "\r")]
    [InlineData(TerminalKey.Z, "\u001a")]
    public void CtrlAndALetter_IsTheMatchingControlByte(TerminalKey key, string expected) =>
        Assert.Equal(expected, Encoded(key, TerminalKeyModifiers.Ctrl));

    [Theory]
    [InlineData(TerminalKey.A, "\u0001")]
    [InlineData(TerminalKey.M, "\r")]
    [InlineData(TerminalKey.Z, "\u001a")]
    public void AddingShiftToACtrlLetter_ChangesNothing_InLegacyEncoding(
        TerminalKey key,
        string expected) =>
        Assert.Equal(
            expected,
            Encoded(key, TerminalKeyModifiers.Ctrl | TerminalKeyModifiers.Shift));

    [Fact]
    public void CtrlC_IsAlwaysTheInterruptByte()
    {
        // A decision, not a default: copy-selection becomes Ctrl+Shift+C in a later phase, there is
        // no selection to copy today, and an interrupt that depends on state is an interrupt nobody
        // can rely on.
        Assert.Equal("\u0003", Encoded(TerminalKey.C, TerminalKeyModifiers.Ctrl));
        Assert.Equal(
            "\u0003",
            Encoded(TerminalKey.C, TerminalKeyModifiers.Ctrl, applicationCursorKeys: true));
    }

    [Fact]
    public void ShiftTab_IsBackTab() =>
        Assert.Equal(Csi("Z"), Encoded(TerminalKey.Tab, TerminalKeyModifiers.Shift));

    [Fact]
    public void CtrlBackspace_IsTheBackspaceByte() =>
        Assert.Equal("\b", Encoded(TerminalKey.Backspace, TerminalKeyModifiers.Ctrl));

    // ---- Alt ----

    [Theory]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.Alt, "a")]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.Alt | TerminalKeyModifiers.Shift, "A")]
    [InlineData(TerminalKey.Enter, TerminalKeyModifiers.Alt, "\r")]
    [InlineData(TerminalKey.Backspace, TerminalKeyModifiers.Alt, "\u007f")]
    [InlineData(TerminalKey.Escape, TerminalKeyModifiers.Alt, Esc)]
    [InlineData(TerminalKey.Space, TerminalKeyModifiers.Alt, " ")]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.Alt | TerminalKeyModifiers.Ctrl, "\u0001")]
    [InlineData(TerminalKey.Space, TerminalKeyModifiers.Alt | TerminalKeyModifiers.Ctrl, "\u0000")]
    public void Alt_PrefixesEscapeOnEveryKeyThatEncodesToBareBytes(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string suffix)
    {
        // Alt+Backspace is the one users notice: it is readline's delete-previous-word, and without
        // the prefix it deletes a character instead.
        Assert.Equal(Esc + suffix, Encoded(key, modifiers));
    }

    // ---- which kind of nothing ----

    [Theory]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.Shift)]
    [InlineData(TerminalKey.Z, TerminalKeyModifiers.Shift)]
    [InlineData(TerminalKey.Space, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.Space, TerminalKeyModifiers.Shift)]
    public void AKeyTheTextPipelineWillDeliver_IsDeliveredAsText(
        TerminalKey key,
        TerminalKeyModifiers modifiers) =>
        Assert.Equal(TerminalKeyDelivery.Text, Delivery(key, modifiers));

    [Theory]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.Ctrl)]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.Alt)]
    [InlineData(TerminalKey.Space, TerminalKeyModifiers.Ctrl)]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.Enter, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.Tab, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.Escape, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.F1, TerminalKeyModifiers.None)]
    public void AKeyThatEncodesItself_IsDeliveredAsASequence(
        TerminalKey key,
        TerminalKeyModifiers modifiers) =>
        Assert.Equal(TerminalKeyDelivery.Sequence, Delivery(key, modifiers));

    [Theory]
    [InlineData(TerminalKeyModifiers.None)]
    [InlineData(TerminalKeyModifiers.Ctrl)]
    [InlineData(TerminalKeyModifiers.Alt)]
    [InlineData(TerminalKeyModifiers.Ctrl | TerminalKeyModifiers.Alt)]
    public void AKeyWithNoNameHere_IsDeclinedRatherThanClaimed(TerminalKeyModifiers modifiers)
    {
        // The distinction the whole controller rests on. An unnamed chord and a plain letter both
        // send no bytes, and a pane that treats them alike deletes the chord: no character ever
        // follows a Ctrl chord, so claiming it sends the keystroke nowhere at all.
        Assert.Equal(TerminalKeyDelivery.None, Delivery(TerminalKey.None, modifiers));
    }
    [Fact]
    public void TheKeysTheTextPipelineCarries_AreExactlyTheLettersAndSpaceWithAtMostShift()
    {
        // Both directions, over the whole cross-product. The per-row check inside Encoded only sees
        // rows that expect bytes, so on its own it would let an encoder answer Sequence for a bare
        // letter and write its byte - which sends every typed character to the shell twice.
        var buffer = new byte[TerminalKeyEncoder.MaxEncodedBytes];

        foreach (var key in Enum.GetValues<TerminalKey>())
        foreach (var modifiers in AllModifiers)
        {
            var delivery = TerminalKeyEncoder.Encode(
                key, modifiers, Modes(applicationCursorKeys: false), buffer, out var written);

            var types = (key == TerminalKey.Space || key is >= TerminalKey.A and <= TerminalKey.Z)
                && (modifiers & ~TerminalKeyModifiers.Shift) == 0;

            Assert.Equal(written > 0, delivery == TerminalKeyDelivery.Sequence);
            Assert.Equal(types, delivery == TerminalKeyDelivery.Text);
        }
    }


    static TerminalKeyModifiers[] AllModifiers =>
    [
        TerminalKeyModifiers.None,
        TerminalKeyModifiers.Shift,
        TerminalKeyModifiers.Alt,
        TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt,
        TerminalKeyModifiers.Ctrl,
        TerminalKeyModifiers.Ctrl | TerminalKeyModifiers.Shift,
        TerminalKeyModifiers.Ctrl | TerminalKeyModifiers.Alt,
        TerminalKeyModifiers.Ctrl | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Shift,
    ];

    static string Csi(string tail) => Esc + "[" + tail;

    static string Ss3(string final) => Esc + "O" + final;

    /// <summary>
    /// The bytes for one key press, read back as Latin-1 so an expectation can be written as the
    /// characters it is. Every sequence this encoder produces is ASCII.
    /// </summary>
    static string Encoded(
        TerminalKey key,
        TerminalKeyModifiers modifiers = TerminalKeyModifiers.None,
        bool applicationCursorKeys = false)
    {
        var buffer = new byte[TerminalKeyEncoder.MaxEncodedBytes];
        var delivery = TerminalKeyEncoder.Encode(
            key,
            modifiers,
            Modes(applicationCursorKeys),
            buffer,
            out var written);

        // Checked on every row rather than once: a sequence is exactly the case that writes bytes,
        // and the two answers drifting apart is the bug the delivery enum exists to make impossible.
        Assert.Equal(written > 0, delivery == TerminalKeyDelivery.Sequence);
        return Encoding.Latin1.GetString(buffer, 0, written);
    }

    static TerminalKeyDelivery Delivery(TerminalKey key, TerminalKeyModifiers modifiers) =>
        TerminalKeyEncoder.Encode(
            key,
            modifiers,
            Modes(applicationCursorKeys: false),
            new byte[TerminalKeyEncoder.MaxEncodedBytes],
            out _);

    static int Length(TerminalKey key, TerminalKeyModifiers modifiers)
    {
        TerminalKeyEncoder.Encode(
            key,
            modifiers,
            Modes(applicationCursorKeys: false),
            new byte[TerminalKeyEncoder.MaxEncodedBytes],
            out var written);
        return written;
    }

    static TerminalModes Modes(bool applicationCursorKeys) => new(
        ApplicationCursorKeys: applicationCursorKeys,
        ApplicationKeypad: false,
        AutoWrap: true,
        AlternateScreen: false,
        BracketedPaste: false,
        FocusReporting: false,
        SynchronizedOutput: false,
        MouseTracking: MouseTracking.Off,
        MouseEncoding: MouseEncoding.X10,
        KeyboardProtocolFlags: 0,
        ModifyOtherKeys: 0);
}
