using System.Text;
using GitBench.Features.Terminal;
using GitBench.Localization;
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
/// The key encoder at its edges: buffers that do not fit, values no keyboard produces, and modes the
/// encoder is handed but does not yet act on.
/// </summary>
/// <remarks>
/// <para>
/// Escape characters are spelled as "\u001b" and never written literally: an escape in a source
/// literal is invisible in every diff and review that follows.
/// </para>
/// <para>
/// Several of these pin a choice the acceptance criteria do not state — what a short buffer does,
/// what an out-of-range modifier bit means, what Ctrl+Tab is. Each such test says so in its own
/// comment, because the point of pinning them is that changing them should be a decision rather
/// than a regression.
/// </para>
/// </remarks>
public class TerminalKeyEncoderEdgeTests
{
    const string Esc = "\u001b";
    const string Csi = Esc + "[";
    const string Ss3 = Esc + "O";

    const TerminalKeyModifiers Shift = TerminalKeyModifiers.Shift;
    const TerminalKeyModifiers Alt = TerminalKeyModifiers.Alt;
    const TerminalKeyModifiers Ctrl = TerminalKeyModifiers.Ctrl;

    // ---- buffer boundaries ----

    [Theory]
    [InlineData(TerminalKey.Enter, TerminalKeyModifiers.None, "\r")]
    [InlineData(TerminalKey.Enter, Alt, Esc + "\r")]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.None, Csi + "A")]
    [InlineData(TerminalKey.F1, TerminalKeyModifiers.None, Ss3 + "P")]
    [InlineData(TerminalKey.Up, Ctrl, Csi + "1;5A")]
    [InlineData(TerminalKey.F12, Ctrl | Alt | Shift, Csi + "24;8~")]
    public void ABufferOfExactlyTheRightSize_IsEnough(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected)
    {
        // The longest sequence this encoder has is seven bytes, so MaxEncodedBytes has slack in it.
        // A caller sizing a buffer from the sequence rather than the constant must still work.
        Assert.Equal(expected, Encoded(key, modifiers, bufferSize: expected.Length));
    }

    [Theory]
    [InlineData(TerminalKey.Enter, TerminalKeyModifiers.None, 0)]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.None, 0)]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.None, 2)]
    [InlineData(TerminalKey.Delete, TerminalKeyModifiers.None, 3)]
    [InlineData(TerminalKey.F12, Ctrl | Alt | Shift, 6)]
    public void ABufferTooSmallForTheSequence_ThrowsInsteadOfSendingHalfOfIt(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        int bufferSize)
    {
        // The criteria do not say. Truncating is the one answer that cannot be right — half an
        // escape sequence leaves the program on the other end parsing the next keystroke as its
        // tail. Returning 0 is worse than throwing, because 0 already means "nothing to send, let
        // the character through", so a short buffer would silently turn Ctrl+C into the letter c.
        // That leaves a thrown precondition, which the only real caller can never trip.
        Assert.ThrowsAny<ArgumentException>(() =>
            TerminalKeyEncoder.Encode(key, modifiers, Quiet, new byte[bufferSize], out _));
    }

    [Theory]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.A, Shift)]
    [InlineData(TerminalKey.Space, TerminalKeyModifiers.None)]
    [InlineData(TerminalKey.None, Ctrl)]
    public void AKeyThatSendsNothing_NeedsNoBufferAtAll(
        TerminalKey key,
        TerminalKeyModifiers modifiers)
    {
        // The size check has to come after the decision, not before it: a key with no encoding must
        // return 0 rather than complain about a buffer it was never going to write to.
        TerminalKeyEncoder.Encode(key, modifiers, Quiet, Span<byte>.Empty, out var written);
        Assert.Equal(0, written);
    }

    [Theory]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.None, 3)]
    [InlineData(TerminalKey.Enter, TerminalKeyModifiers.None, 1)]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.None, 0)]
    public void NothingBeyondTheReturnedCountIsWrittenTo(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        int expectedLength)
    {
        // The caller slices by the return value, so a stale byte past it would be invisible here and
        // visible the first time someone forgets to slice.
        var buffer = new byte[TerminalKeyEncoder.MaxEncodedBytes];
        Array.Fill(buffer, (byte)0xFF);

        TerminalKeyEncoder.Encode(key, modifiers, Quiet, buffer, out var written);

        Assert.Equal(expectedLength, written);
        Assert.True(
            buffer.AsSpan(written).IndexOfAnyExcept((byte)0xFF) < 0,
            $"{key} wrote past the {written} bytes it reported.");
    }

    [Fact]
    public void NoKeyAndNoModifierCombination_CanOverrunAMaxEncodedBytesBuffer()
    {
        // Cast values included on purpose: MaxEncodedBytes is the size of a stack buffer, and a
        // bound that only holds for the values a keyboard happens to produce is not a bound.
        const int Guard = 8;
        var buffer = new byte[TerminalKeyEncoder.MaxEncodedBytes + Guard];

        foreach (var key in AllKeysIncludingUndefined)
        foreach (var raw in Enumerable.Range(0, 256))
        foreach (var modes in ModeSpread)
        {
            Array.Fill(buffer, (byte)0xFF);

            TerminalKeyEncoder.Encode(
                key,
                (TerminalKeyModifiers)raw,
                modes,
                buffer.AsSpan(0, TerminalKeyEncoder.MaxEncodedBytes),
                out var written);

            Assert.InRange(written, 0, TerminalKeyEncoder.MaxEncodedBytes);
            Assert.True(
                buffer.AsSpan(TerminalKeyEncoder.MaxEncodedBytes).IndexOfAnyExcept((byte)0xFF) < 0,
                $"{key} + {(TerminalKeyModifiers)raw} wrote past MaxEncodedBytes.");
        }
    }

    // ---- values no keyboard produces ----

    [Theory]
    [InlineData(9999, TerminalKeyModifiers.None)]
    [InlineData(9999, Ctrl)]
    [InlineData(-1, TerminalKeyModifiers.None)]
    [InlineData(-1, Alt)]
    [InlineData(1000, Ctrl | Alt | Shift)]
    public void AKeyOutsideTheEnum_EncodesToNothing(int rawKey, TerminalKeyModifiers modifiers)
    {
        // A translation table that grows a key the encoder has not learned yet must degrade to
        // silence, not to whatever byte sits at that offset in a jump table.
        Assert.Equal(0, Length((TerminalKey)rawKey, modifiers));
    }

    [Theory]
    [InlineData(8, Csi + "C")]
    [InlineData(248, Csi + "C")]
    [InlineData(8 | 4, Csi + "1;5C")]
    [InlineData(255, Csi + "1;8C")]
    public void ModifierBitsAboveTheKnownThree_AreIgnoredRatherThanCounted(
        int rawModifiers,
        string expected)
    {
        // Not defensive coding for its own sake: the modifier parameter is arithmetic on the bits,
        // so an unmasked stray bit becomes CSI 1;256C — three bytes longer than MaxEncodedBytes
        // allows, and a sequence no program has ever seen.
        Assert.Equal(
            expected,
            Encoded(TerminalKey.Right, (TerminalKeyModifiers)rawModifiers));
    }

    [Fact]
    public void ModifierBitsAboveTheKnownThree_AreIgnoredOnBareByteKeysToo() =>
        Assert.Equal("\u0001", Encoded(TerminalKey.A, (TerminalKeyModifiers)(255 & ~2)));

    [Theory]
    [InlineData(TerminalKeyModifiers.None)]
    [InlineData(Shift)]
    [InlineData(Alt)]
    [InlineData(Shift | Alt)]
    [InlineData(Ctrl)]
    [InlineData(Ctrl | Shift)]
    [InlineData(Ctrl | Alt)]
    [InlineData(Ctrl | Alt | Shift)]
    public void TheAbsentKey_EncodesToNothingUnderEveryModifier(TerminalKeyModifiers modifiers)
    {
        // TerminalKey.None is what the controller hands over for every key it has no name for, and
        // it arrives carrying whatever the user was holding. Alt in particular must not turn it
        // into a lone escape byte.
        Assert.Equal(0, Length(TerminalKey.None, modifiers));
    }

    // ---- modes the encoder is handed but does not act on ----

    [Theory]
    [InlineData(1, 0)]
    [InlineData(31, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(31, 2)]
    [InlineData(-1, -1)]
    public void ANegotiatedKeyboardProtocol_ChangesNothingYet(int protocolFlags, int modifyOtherKeys)
    {
        // Legacy is all that is implemented, and a program that asked for CSI-u must still get
        // legacy rather than a half-built version of what it asked for. Pinned so that implementing
        // kitty later is a deliberate edit to this test rather than a silent change of behaviour.
        var modes = Quiet with
        {
            KeyboardProtocolFlags = protocolFlags,
            ModifyOtherKeys = modifyOtherKeys,
        };

        Assert.Equal("\u0003", Encoded(TerminalKey.C, Ctrl, modes));
        Assert.Equal(Csi + "A", Encoded(TerminalKey.Up, modes: modes));
        Assert.Equal(TerminalKeyDelivery.Text, TerminalKeyEncoder.Encode(
            TerminalKey.A,
            TerminalKeyModifiers.None,
            modes,
            new byte[TerminalKeyEncoder.MaxEncodedBytes],
            out _));
    }

    [Theory]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.None, Csi + "A")]
    [InlineData(TerminalKey.Enter, TerminalKeyModifiers.None, "\r")]
    [InlineData(TerminalKey.F1, TerminalKeyModifiers.None, Ss3 + "P")]
    [InlineData(TerminalKey.Delete, TerminalKeyModifiers.None, Csi + "3~")]
    [InlineData(TerminalKey.C, Ctrl, "\u0003")]
    [InlineData(TerminalKey.Tab, Shift, Csi + "Z")]
    public void ModesThatAreNotAboutKeys_LeaveTheEncodingAlone(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected)
    {
        // Alt-screen, bracketed paste, mouse tracking and the rest travel in the same record as the
        // one mode that does matter. Reading the wrong field of it is a one-character mistake.
        Assert.Equal(expected, Encoded(key, modifiers, Noisy));
    }

    [Fact]
    public void ApplicationCursorKeys_IsReadOnEveryCall_NotRememberedFromTheLastOne()
    {
        // The encoder is static, so anything it caches is cached for the life of the process and for
        // every terminal pane at once.
        Assert.Equal(Ss3 + "A", Encoded(TerminalKey.Up, modes: ApplicationCursor));
        Assert.Equal(Csi + "A", Encoded(TerminalKey.Up, modes: Quiet));
    }

    // ---- where Alt folds in and where it prefixes ----

    [Theory]
    [InlineData(TerminalKey.Up, Alt, Csi + "1;3A")]
    [InlineData(TerminalKey.Home, Alt, Csi + "1;3H")]
    [InlineData(TerminalKey.End, Alt, Csi + "1;3F")]
    [InlineData(TerminalKey.Delete, Alt, Csi + "3;3~")]
    [InlineData(TerminalKey.Insert, Alt, Csi + "2;3~")]
    [InlineData(TerminalKey.PageUp, Alt, Csi + "5;3~")]
    [InlineData(TerminalKey.F1, Alt, Csi + "1;3P")]
    [InlineData(TerminalKey.F5, Alt, Csi + "15;3~")]
    [InlineData(TerminalKey.Left, Ctrl | Alt, Csi + "1;7D")]
    [InlineData(TerminalKey.PageDown, Ctrl | Alt | Shift, Csi + "6;8~")]
    public void OnASequenceWithAModifierParameter_AltGoesIntoTheParameterAndNotInFrontOfIt(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected)
    {
        // This is the seam between the two Alt rules, and the easy way to get it wrong is to prefix
        // escape at the end of the function for every key that had Alt held. That produces
        // ESC ESC [ 1 ; 3 A, which readline reads as "escape, then a modified up arrow".
        Assert.Equal(expected, Encoded(key, modifiers));
    }

    [Fact]
    public void AltOnACursorKey_FoldsIn_EvenUnderApplicationCursorKeys() =>
        Assert.Equal(Csi + "1;3A", Encoded(TerminalKey.Up, Alt, ApplicationCursor));

    // ---- the full modifier table, one key per encoding family ----

    [Theory]
    [InlineData(TerminalKeyModifiers.None, Csi + "3~")]
    [InlineData(Shift, Csi + "3;2~")]
    [InlineData(Alt, Csi + "3;3~")]
    [InlineData(Shift | Alt, Csi + "3;4~")]
    [InlineData(Ctrl, Csi + "3;5~")]
    [InlineData(Ctrl | Shift, Csi + "3;6~")]
    [InlineData(Ctrl | Alt, Csi + "3;7~")]
    [InlineData(Ctrl | Alt | Shift, Csi + "3;8~")]
    public void EveryModifierSubsetOfATildeKey(TerminalKeyModifiers modifiers, string expected) =>
        Assert.Equal(expected, Encoded(TerminalKey.Delete, modifiers));

    [Theory]
    [InlineData(TerminalKeyModifiers.None, Ss3 + "R")]
    [InlineData(Shift, Csi + "1;2R")]
    [InlineData(Alt, Csi + "1;3R")]
    [InlineData(Shift | Alt, Csi + "1;4R")]
    [InlineData(Ctrl, Csi + "1;5R")]
    [InlineData(Ctrl | Shift, Csi + "1;6R")]
    [InlineData(Ctrl | Alt, Csi + "1;7R")]
    [InlineData(Ctrl | Alt | Shift, Csi + "1;8R")]
    public void EveryModifierSubsetOfALowFunctionKey(
        TerminalKeyModifiers modifiers,
        string expected)
    {
        // The one family that changes shape under a modifier: SS3 bare, CSI with a parameter once
        // anything is held, and the final byte survives the change.
        Assert.Equal(expected, Encoded(TerminalKey.F3, modifiers));
    }

    [Theory]
    [InlineData(TerminalKeyModifiers.None, "\u007f")]
    [InlineData(Shift, "\u007f")]
    [InlineData(Alt, Esc + "\u007f")]
    [InlineData(Shift | Alt, Esc + "\u007f")]
    [InlineData(Ctrl, "\b")]
    [InlineData(Ctrl | Shift, "\b")]
    [InlineData(Ctrl | Alt, Esc + "\b")]
    [InlineData(Ctrl | Alt | Shift, Esc + "\b")]
    public void EveryModifierSubsetOfABareByteKey(TerminalKeyModifiers modifiers, string expected)
    {
        // Three rules interacting on one key: Shift is dropped, Ctrl changes the byte, Alt prefixes
        // whatever the other two settled on.
        Assert.Equal(expected, Encoded(TerminalKey.Backspace, modifiers));
    }

    [Theory]
    [InlineData(TerminalKeyModifiers.None, Esc)]
    [InlineData(Shift, Esc)]
    [InlineData(Ctrl, Esc)]
    [InlineData(Ctrl | Shift, Esc)]
    [InlineData(Alt, Esc + Esc)]
    [InlineData(Shift | Alt, Esc + Esc)]
    [InlineData(Ctrl | Alt, Esc + Esc)]
    [InlineData(Ctrl | Alt | Shift, Esc + Esc)]
    public void EscapeKeepsItsByte_WhateverIsHeldWithIt(
        TerminalKeyModifiers modifiers,
        string expected)
    {
        // Escape is the key a vim user presses hardest and most often, and legacy encoding has
        // nothing to say about a modified one. Sending anything other than a plain escape for
        // Ctrl+Escape leaves insert mode without leaving insert mode.
        Assert.Equal(expected, Encoded(TerminalKey.Escape, modifiers));
    }

    [Theory]
    [InlineData(Ctrl, "\t")]
    [InlineData(Ctrl | Shift, Csi + "Z")]
    [InlineData(Alt, Esc + "\t")]
    [InlineData(Shift | Alt, Csi + "Z")]
    [InlineData(Ctrl | Alt | Shift, Csi + "Z")]
    public void TabUnderModifiersLegacyHasNoFormFor_DropsWhatItCannotSay(
        TerminalKeyModifiers modifiers,
        string expected)
    {
        // The criteria give Tab and Shift+Tab and stop. The rule taken here is the one A14 already
        // applies to Enter: a modifier legacy cannot express is dropped rather than invented. So
        // Shift wins (it has a form, CSI Z), Ctrl is dropped, and Alt prefixes only in the case that
        // is still a bare byte. Alt+Shift+Tab therefore loses its Alt, because CSI Z has nowhere to
        // put it. The alternative reading is ESC CSI Z, which is what xterm's altSendsEscape does.
        Assert.Equal(expected, Encoded(TerminalKey.Tab, modifiers));
    }

    // ---- control chords at their collisions ----

    [Theory]
    [InlineData(TerminalKey.H, "\b")]
    [InlineData(TerminalKey.I, "\t")]
    [InlineData(TerminalKey.J, "\n")]
    [InlineData(TerminalKey.M, "\r")]
    public void ACtrlLetterThatLandsOnAControlKeysByte_StillEncodesByPosition(
        TerminalKey key,
        string expected)
    {
        // Ctrl+I is genuinely a tab and Ctrl+M genuinely a carriage return. A table that tried to
        // keep these distinct would be inventing bytes no shell reads.
        Assert.Equal(expected, Encoded(key, Ctrl));
    }

    [Fact]
    public void TheTwentySixCtrlLetters_AreTwentySixDistinctControlBytes()
    {
        // A hand-written switch with a copy-paste slip sends two different letters as the same byte,
        // and the only symptom is that one readline binding stops working.
        var bytes = Enumerable
            .Range((int)TerminalKey.A, 26)
            .Select(k => Encoded((TerminalKey)k, Ctrl))
            .ToArray();

        Assert.All(bytes, b => Assert.InRange(b[0], '\u0001', '\u001a'));
        Assert.All(bytes, b => Assert.Equal(1, b.Length));
        Assert.Equal(26, bytes.Distinct().Count());
    }

    [Fact]
    public void CtrlSpace_IsNul_EvenWithShiftHeld() =>
        Assert.Equal("\u0000", Encoded(TerminalKey.Space, Ctrl | Shift));

    [Theory]
    [InlineData(TerminalKey.Space, Alt | Shift, " ")]
    [InlineData(TerminalKey.Z, Alt | Shift, "Z")]
    [InlineData(TerminalKey.A, Ctrl | Alt | Shift, "\u0001")]
    [InlineData(TerminalKey.Space, Ctrl | Alt | Shift, "\u0000")]
    public void ShiftOnlyChangesTheLetter_NeverTheControlByte(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string suffix) =>
        Assert.Equal(Esc + suffix, Encoded(key, modifiers));

    [Fact]
    public void ShiftInsert_IsStillTheLegacySequence()
    {
        // Terminals eventually bind Shift+Insert to paste. Until they do, it is an editing key like
        // any other, and when paste lands the interception belongs in the controller, not here.
        Assert.Equal(Csi + "2;2~", Encoded(TerminalKey.Insert, Shift));
    }

    // ---- helpers ----

    static readonly TerminalModes Quiet = new(
        ApplicationCursorKeys: false,
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

    static readonly TerminalModes ApplicationCursor = Quiet with { ApplicationCursorKeys = true };

    /// <summary>Every mode that is not about keys, switched to its least default value.</summary>
    static readonly TerminalModes Noisy = new(
        ApplicationCursorKeys: false,
        ApplicationKeypad: true,
        AutoWrap: false,
        AlternateScreen: true,
        BracketedPaste: true,
        FocusReporting: true,
        SynchronizedOutput: true,
        MouseTracking: MouseTracking.AnyEvent,
        MouseEncoding: MouseEncoding.Sgr,
        KeyboardProtocolFlags: 31,
        ModifyOtherKeys: 2);

    static TerminalModes[] ModeSpread =>
    [
        Quiet,
        ApplicationCursor,
        Noisy,
        Quiet with { KeyboardProtocolFlags = -1, ModifyOtherKeys = -1 },
    ];

    static IEnumerable<TerminalKey> AllKeysIncludingUndefined =>
        Enum.GetValues<TerminalKey>().Concat([(TerminalKey)(-1), (TerminalKey)9999]);

    /// <summary>
    /// The bytes for one key press, read back as Latin-1 so an expectation can be written as the
    /// characters it is. Every sequence this encoder produces is ASCII.
    /// </summary>
    static string Encoded(
        TerminalKey key,
        TerminalKeyModifiers modifiers = TerminalKeyModifiers.None,
        TerminalModes? modes = null,
        int bufferSize = TerminalKeyEncoder.MaxEncodedBytes)
    {
        var buffer = new byte[bufferSize];
        var delivery = TerminalKeyEncoder.Encode(key, modifiers, modes ?? Quiet, buffer, out var written);

        Assert.Equal(written > 0, delivery == TerminalKeyDelivery.Sequence);
        return Encoding.Latin1.GetString(buffer, 0, written);
    }

    static int Length(TerminalKey key, TerminalKeyModifiers modifiers)
    {
        TerminalKeyEncoder.Encode(
            key,
            modifiers,
            Quiet,
            new byte[TerminalKeyEncoder.MaxEncodedBytes],
            out var written);
        return written;
    }
}

/// <summary>
/// The terminal pane's keyboard at its edges: lock keys, the boundaries of the reserved set, keys
/// the encoder has no name for, and the states a pane passes through on its way out — hidden, blurred,
/// and behind a shell that has gone.
/// </summary>
/// <remarks>
/// The shell here is a hand-written stand-in rather than a real session, because these tests need to
/// move it into states a real one only reaches by luck: modes changing between two keystrokes, input
/// stopping mid-sentence. What reaches the wire is asserted as bytes either way.
/// </remarks>
public class TerminalInputControllerEdgeTests
{
    const string Esc = "\u001b";
    const string Csi = Esc + "[";

    // ---- lock keys ----

    [Theory]
    [InlineData(KeyboardKey.RightArrow, InputModifiers.Control | InputModifiers.NumLock, Csi + "1;5C")]
    [InlineData(KeyboardKey.RightArrow, InputModifiers.Control | InputModifiers.CapsLock, Csi + "1;5C")]
    [InlineData(
        KeyboardKey.RightArrow,
        InputModifiers.Shift | InputModifiers.NumLock | InputModifiers.CapsLock,
        Csi + "1;2C")]
    [InlineData(KeyboardKey.C, InputModifiers.Control | InputModifiers.NumLock, "\u0003")]
    [InlineData(KeyboardKey.A, InputModifiers.Alt | InputModifiers.NumLock, Esc + "a")]
    public void ALockKeyRidingAlongWithARealModifier_StaysOutOfTheSequence(
        KeyboardKey key,
        InputModifiers modifiers,
        string expected)
    {
        // Num lock is on for most of the world all of the time, so every one of these is what the
        // user's actual keyboard sends. Copying the modifier bits straight across turns Ctrl+Right
        // into CSI 1;53C.
        using var pane = Pane.Focused();

        pane.Press(key, modifiers);

        Assert.Equal(expected, pane.Shell.Text);
    }

    [Fact]
    public void CapsLock_DoesNotStandInForShift()
    {
        // Alt+A is one of the two chords whose byte depends on case, and caps lock is not a shift:
        // the operating system has already decided what character the key makes, and for an Alt
        // chord the encoder is only asked which letter position was pressed.
        using var pane = Pane.Focused();

        pane.Press(KeyboardKey.A, InputModifiers.Alt | InputModifiers.CapsLock);

        Assert.Equal(Esc + "a", pane.Shell.Text);
    }

    [Fact]
    public void CtrlAndADigitWithALockKeyHeld_IsStillTheApplicationsShortcut()
    {
        using var pane = Pane.Focused();

        var claim = pane.Press(KeyboardKey.Alpha1, InputModifiers.Control | InputModifiers.NumLock);

        Assert.Empty(pane.Shell.Written);
        Assert.Equal(KeyClaim.None, claim);
    }

    // ---- the edges of the reserved set ----

    [Theory]
    [InlineData(InputModifiers.None, Csi + "15~")]
    [InlineData(InputModifiers.NumLock | InputModifiers.CapsLock, Csi + "15~")]
    [InlineData(InputModifiers.Shift, Csi + "15;2~")]
    [InlineData(InputModifiers.Control, Csi + "15;5~")]
    [InlineData(InputModifiers.Alt, Csi + "15;3~")]
    public void F5_IsAFunctionKeyTheTerminalWants_NotTheApplicationsRefresh(
        InputModifiers modifiers,
        string expected)
    {
        // The reserved set is mode switching and repo hotkeys, and a forced refresh is neither. F5
        // is a key htop, mc and vim all bind, and reserving it would make it unreachable from this
        // pane with no way to send it — while losing the refresh only costs a click elsewhere first.
        using var pane = Pane.Focused();

        var claim = pane.Press(KeyboardKey.F5, modifiers);

        Assert.Equal(expected, pane.Shell.Text);
        Assert.Equal(KeyClaim.Command, claim);
    }

    [Theory]
    [InlineData(KeyboardKey.C, InputModifiers.Super)]
    [InlineData(KeyboardKey.A, InputModifiers.Super | InputModifiers.Control)]
    [InlineData(KeyboardKey.LeftArrow, InputModifiers.Super | InputModifiers.Shift)]
    [InlineData(KeyboardKey.Escape, InputModifiers.Super)]
    public void ASuperChord_IsTheApplicationsEvenWhenTheTerminalCouldEncodeIt(
        KeyboardKey key,
        InputModifiers modifiers)
    {
        // Super is the one modifier the terminal never claims, so the test that matters is the one
        // where the underlying key does have an encoding and is given up anyway.
        using var pane = Pane.Focused();

        var claim = pane.Press(key, modifiers);

        Assert.Empty(pane.Shell.Written);
        Assert.Equal(KeyClaim.None, claim);
        Assert.Contains((key, modifiers), pane.App.Keys);
    }

    [Theory]
    [InlineData(KeyboardKey.Alpha0, InputModifiers.Control)]
    [InlineData(KeyboardKey.Numpad0, InputModifiers.Control)]
    [InlineData(KeyboardKey.Slash, InputModifiers.Control)]
    [InlineData(KeyboardKey.LeftBracket, InputModifiers.Control)]
    [InlineData(KeyboardKey.Backslash, InputModifiers.Control)]
    [InlineData(KeyboardKey.Minus, InputModifiers.Control | InputModifiers.Alt)]
    public void AChordTheEncoderHasNoNameFor_IsLeftForTheApplicationRatherThanSwallowed(
        KeyboardKey key,
        InputModifiers modifiers)
    {
        // The hazard behind this one: Encode returns 0 both for "the text pipeline will deliver
        // this" and for "I have never heard of this key". Claiming the second as text consumes the
        // keystroke, and no character ever follows a Ctrl chord — so Ctrl+0 would reach neither the
        // shell nor the application and simply stop existing.
        using var pane = Pane.Focused();

        var claim = pane.Press(key, modifiers);

        Assert.Empty(pane.Shell.Written);
        Assert.Equal(KeyClaim.None, claim);
        Assert.Contains((key, modifiers), pane.App.Keys);
    }

    [Theory]
    [InlineData(KeyboardKey.Alpha0, InputModifiers.None)]
    [InlineData(KeyboardKey.Alpha0, InputModifiers.Shift)]
    [InlineData(KeyboardKey.Slash, InputModifiers.None)]
    [InlineData(KeyboardKey.GraveAccent, InputModifiers.None)]
    [InlineData(KeyboardKey.Minus, InputModifiers.Shift | InputModifiers.CapsLock)]
    public void AnUnnamedKeyWithNothingCommandLikeHeld_IsStillClaimedSoItCanType(
        KeyboardKey key,
        InputModifiers modifiers)
    {
        // The other half of the same rule. Digits and punctuation have no TerminalKey, and if the
        // pane declined them the application's keybindings would see every character the user types.
        using var pane = Pane.Focused();

        var claim = pane.Press(key, modifiers);

        Assert.Empty(pane.Shell.Written);
        Assert.Equal(KeyClaim.Text, claim);
    }

    [Fact]
    public void AnAltGrChord_IsIndistinguishableFromCtrlAlt_AndCostsTheCharacter()
    {
        // Windows reports AltGr as Control+Alt, so on a German layout AltGr+Q — the way to type '@' —
        // arrives here as a Ctrl+Alt chord and is encoded as one. A11 asks for exactly this, so it is
        // pinned rather than worked around; the cost is that '@' is untypable on those layouts, and
        // the fix (deferring to whether the OS produced a character) is a change to this test.
        using var pane = Pane.Focused();

        var claim = pane.Press(KeyboardKey.Q, InputModifiers.Control | InputModifiers.Alt);

        Assert.Equal(Esc + "\u0011", pane.Shell.Text);
        Assert.Equal(KeyClaim.Command, claim);
    }

    // ---- dispatch shape ----

    [Fact]
    public void AnEncodedKey_IsWrittenOnce_ThoughTheControllerIsBothFocusedAndOnTheHoverPath()
    {
        // The input system dispatches the focus holder, then walks the hover path forward, then
        // walks it back. A focused pane that is also hovered sits in all three, so anything it does
        // without claiming the key it does three times.
        using var pane = Pane.Focused();

        pane.Press(KeyboardKey.UpArrow);

        Assert.Equal(Csi + "A", pane.Shell.Text);
        Assert.Equal(1, pane.Shell.Writes);
    }

    [Theory]
    [InlineData(KeyboardKey.C, InputModifiers.Control)]
    [InlineData(KeyboardKey.UpArrow, InputModifiers.Shift)]
    [InlineData(KeyboardKey.A, InputModifiers.None)]
    public void AKeyRelease_IsNeitherEncodedNorClaimed_WhateverWasHeldWithIt(
        KeyboardKey key,
        InputModifiers modifiers)
    {
        // Releasing Ctrl+C must not send a second interrupt, and a release claimed as text would
        // suppress nothing but would still stop the application seeing the key go up.
        using var pane = Pane.Focused();

        var claim = pane.Release(key, modifiers);

        Assert.Empty(pane.Shell.Written);
        Assert.Equal(KeyClaim.None, claim);
    }

    [Fact]
    public void TwoKeysPressedBeforeEitherCharacterArrives_KeepTheirOrder()
    {
        // Key events and their characters are separate OS callbacks, so a controller that remembered
        // "the key I am waiting for a character for" would drop one of these under fast typing.
        using var pane = Pane.Focused();

        pane.Harness.KeyDown(KeyboardKey.H);
        pane.Harness.KeyDown(KeyboardKey.I);
        pane.Harness.SendText(new Rune('h'));
        pane.Harness.SendText(new Rune('i'));

        Assert.Equal("hi", pane.Shell.Text);
    }

    [Fact]
    public void FocusTakenAwayBetweenAKeyAndItsCharacter_LeavesTheCharacterToWhoeverHasItNow()
    {
        using var pane = Pane.Focused();
        pane.Press(KeyboardKey.A);

        pane.Harness.Input.StealFocus(pane.App);
        pane.Harness.SendText(new Rune('a'));

        Assert.Empty(pane.Shell.Written);
    }

    // ---- characters ----

    [Theory]
    [InlineData(0x7F, new byte[] { 0x7F })]
    [InlineData(0x80, new byte[] { 0xC2, 0x80 })]
    [InlineData(0x7FF, new byte[] { 0xDF, 0xBF })]
    [InlineData(0x800, new byte[] { 0xE0, 0xA0, 0x80 })]
    [InlineData(0xFFFD, new byte[] { 0xEF, 0xBF, 0xBD })]
    [InlineData(0x10000, new byte[] { 0xF0, 0x90, 0x80, 0x80 })]
    [InlineData(0x10FFFF, new byte[] { 0xF4, 0x8F, 0xBF, 0xBF })]
    [InlineData(0x301, new byte[] { 0xCC, 0x81 })]
    public void ACharacterAtEveryUtf8LengthBoundary_ReachesTheShellWhole(
        int scalar,
        byte[] expected)
    {
        // Each row is one byte either side of a UTF-8 length change, plus a combining acute — what a
        // dead key commits on its own. A cast to byte, or an ASCII encoder, passes for 'a' and fails
        // for every one of these.
        using var pane = Pane.Focused();

        pane.Harness.SendText(new Rune(scalar));

        Assert.Equal(expected, pane.Shell.Written);
    }

    [Fact]
    public void ACharacterArrivingWithNoShell_IsNotWrittenAnywhere()
    {
        using var pane = Pane.Focused(Shell.NotStarted());

        pane.Harness.SendText(new Rune('x'));

        Assert.Empty(pane.Shell.Written);
    }

    [Fact]
    public void ACharacterArrivingWhileThePaneIsHidden_IsNotWritten()
    {
        using var pane = Pane.Focused();
        pane.View.IsVisible = false;

        pane.Harness.SendText(new Rune('x'));

        Assert.Empty(pane.Shell.Written);
    }

    // ---- the shell going away underneath ----

    [Theory]
    [InlineData(KeyboardKey.A, InputModifiers.None)]
    [InlineData(KeyboardKey.Alpha0, InputModifiers.None)]
    public void AfterTheShellIsGone_EvenTheKeysThatOnlyTypeAreHandedBack(
        KeyboardKey key,
        InputModifiers modifiers)
    {
        // The gate belongs in front of the delivery switch, not on the send. A pane that only stops
        // sending bytes goes on eating every letter typed at a shell that has already exited.
        using var pane = Pane.Focused();
        pane.Shell.IsAcceptingInput = false;

        var claim = pane.Press(key, modifiers);

        Assert.Equal(KeyClaim.None, claim);
        Assert.Contains((key, modifiers), pane.App.Keys);
    }

    [Theory]
    [InlineData(KeyboardKey.Menu)]
    [InlineData(KeyboardKey.F13)]
    [InlineData(KeyboardKey.PrintScreen)]
    public void AKeyThatSendsNothingAndTypesNothing_IsLeftForTheApplication(KeyboardKey key)
    {
        // Claiming these as text would stop them dead: propagation ends and no character ever
        // arrives to justify it. They are as silent as Shift is, without being modifiers.
        using var pane = Pane.Focused();

        var claim = pane.Press(key);

        Assert.Empty(pane.Shell.Written);
        Assert.Equal(KeyClaim.None, claim);
        Assert.Contains((key, InputModifiers.None), pane.App.Keys);
    }

    [Fact]
    public void AShellThatHasStoppedTakingInput_DeclinesKeysInsteadOfEatingThem()
    {
        // The user types 'exit', and then keeps typing. Every keystroke after that has to reach the
        // application, because there is nothing else left in the pane to receive it.
        using var pane = Pane.Focused();
        pane.Shell.IsAcceptingInput = false;

        var claim = pane.Press(KeyboardKey.UpArrow);

        Assert.Empty(pane.Shell.Written);
        Assert.Equal(KeyClaim.None, claim);
        Assert.Contains((KeyboardKey.UpArrow, InputModifiers.None), pane.App.Keys);
    }

    [Fact]
    public void TheEncodingFollowsTheShellsModesAtThatKeystroke_NotTheOnesItStartedWith()
    {
        // A full-screen program turns application cursor keys on when it starts and off when it
        // exits, both while the pane keeps the same controller and the same focus.
        using var pane = Pane.Focused();

        pane.Press(KeyboardKey.UpArrow);
        pane.Shell.Modes = pane.Shell.Modes with { ApplicationCursorKeys = true };
        pane.Press(KeyboardKey.UpArrow);

        Assert.Equal(Csi + "A" + Esc + "OA", pane.Shell.Text);
    }

    // ---- hidden and blurred ----

    [Fact]
    public void AHiddenPaneThatNoLongerHasFocus_StillDeclinesWhatItsStaleHoverPathBrings()
    {
        // The hover path is rebuilt on mouse movement, not on visibility, so a pane hidden by the
        // mode switch stays in the dispatch path until the pointer next moves — reachable, unfocused
        // and invisible at once, which is the one combination nothing else covers.
        using var pane = Pane.Focused();
        pane.View.IsVisible = false;
        pane.Harness.Input.Blur(pane.Controller);

        var claim = pane.Press(KeyboardKey.UpArrow);

        Assert.Empty(pane.Shell.Written);
        Assert.Equal(KeyClaim.None, claim);
        Assert.Contains((KeyboardKey.UpArrow, InputModifiers.None), pane.App.Keys);
    }

    // ---- the mouse ----

    [Fact]
    public void APressOutsideThePane_DoesNotTakeTheKeyboard()
    {
        // Reachable because the pane is still on the hover path when a press lands somewhere else in
        // the same dispatch — a pane that focused itself on any press it saw would fight whatever
        // was actually clicked.
        using var pane = Pane.Hovered();

        pane.PressMouse(900f, 700f);

        Assert.Same(pane.App, pane.Harness.Input.FocusedComponent);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ARightOrMiddlePress_AlsoTakesTheKeyboard(int button)
    {
        // Any button, matching DiffWindowKeyController, which filters on state and bounds but not on
        // which button. Middle-click paste and a right-click menu will both want the pane focused
        // when they land, and a pointer inside the pane is the user pointing at the pane whichever
        // finger they used.
        using var pane = Pane.Hovered();

        pane.PressMouse(400f, 300f, new MouseButton(button));

        Assert.Same(pane.Controller, pane.Harness.Input.FocusedComponent);
    }

    [Fact]
    public void AButtonRelease_DoesNotTakeTheKeyboard()
    {
        using var pane = Pane.Hovered();

        pane.SendMouse(400f, 300f, MouseButton.Left, InputState.Released);

        Assert.Same(pane.App, pane.Harness.Input.FocusedComponent);
    }

    [Fact]
    public void WhileTheProgramIsTrackingTheMouse_TheClickIsStillNotSwallowedYet()
    {
        // Mouse reporting is a later phase. Pinned now so that implementing it is a deliberate edit
        // to this test rather than a silent change to what a click in the pane means.
        using var pane = Pane.Focused();
        pane.Shell.Modes = pane.Shell.Modes with { MouseTracking = MouseTracking.ButtonEvent };

        var consumed = pane.PressMouse(400f, 300f);

        Assert.False(consumed);
        Assert.Same(pane.Controller, pane.Harness.Input.FocusedComponent);
    }

    [Fact]
    public void APressWhileThePaneIsHidden_DoesNotSwallowTheClickEither()
    {
        using var pane = Pane.Focused();
        pane.View.IsVisible = false;

        var consumed = pane.PressMouse(400f, 300f);

        Assert.False(consumed, "A hidden pane consumed a click meant for what replaced it.");
        Assert.True(pane.App.SawMousePress);
    }

    [Fact]
    public void AHiddenPane_GivesTheKeyboardBackOnAClickToo()
    {
        // Same rule as the key path: an invisible pane must not be holding the keyboard. Split out
        // because it is a separate obligation, and a controller that only blurs from its key handler
        // leaves focus stranded whenever the user clicks before typing.
        using var pane = Pane.Focused();
        pane.View.IsVisible = false;

        pane.PressMouse(400f, 300f);

        Assert.NotSame(pane.Controller, pane.Harness.Input.FocusedComponent);
    }

    // ---- helpers ----

    /// <summary>
    /// A mounted terminal pane: the grid view, its input controller, a stand-in shell, and a
    /// stand-in for the application's keybindings, registered on the same view so it sees whatever
    /// the terminal declines.
    /// </summary>
    sealed class Pane : IDisposable
    {
        Pane(
            GuiTestHarness harness,
            TerminalGridView view,
            TerminalInputController controller,
            AppKeybinds app,
            Shell shell)
        {
            Harness = harness;
            View = view;
            Controller = controller;
            App = app;
            Shell = shell;
        }

        public GuiTestHarness Harness { get; }
        public TerminalGridView View { get; }
        public TerminalInputController Controller { get; }
        public AppKeybinds App { get; }
        public Shell Shell { get; }

        public static Pane Create(Shell? shell = null)
        {
            var terminal = shell ?? Shell.Live();
            var app = new AppKeybinds();
            TerminalGridView? view = null;
            TerminalInputController? controller = null;

            var harness = GuiTestHarness.Create(
                ctx =>
                {
                    var input = ctx.Require<InputSystem>();
                    view = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>());
                    controller = new TerminalInputController(view, input, terminal);
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

            return new Pane(harness, view!, controller!, app, terminal);
        }

        public static Pane Focused(Shell? shell = null)
        {
            var pane = Create(shell);
            pane.Harness.MoveTo(400f, 300f);
            pane.Harness.Input.StealFocus(pane.Controller);
            return pane;
        }

        /// <summary>On the dispatch path but not holding the keyboard: the application does.</summary>
        public static Pane Hovered(Shell? shell = null)
        {
            var pane = Create(shell);
            pane.Harness.MoveTo(400f, 300f);
            pane.Harness.Input.StealFocus(pane.App);
            return pane;
        }

        public KeyClaim Press(KeyboardKey key, InputModifiers modifiers = InputModifiers.None) =>
            SendKey(key, InputState.Pressed, modifiers);

        public KeyClaim Release(KeyboardKey key, InputModifiers modifiers = InputModifiers.None) =>
            SendKey(key, InputState.Released, modifiers);

        /// <summary>
        /// Dispatches one key through the real input system and hands back how it was claimed. The
        /// harness's own PressKey discards the event, and command-versus-text-versus-unclaimed is
        /// the whole contract here.
        /// </summary>
        KeyClaim SendKey(KeyboardKey key, InputState state, InputModifiers modifiers)
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

        public bool PressMouse(float x, float y, MouseButton? button = null) =>
            SendMouse(x, y, button ?? MouseButton.Left, InputState.Pressed);

        /// <summary>
        /// Dispatches one mouse button at an arbitrary point, without moving the pointer there. The
        /// harness's Click moves first, which would rebuild the hover path and make "a press the
        /// pane sees but did not happen inside it" unreachable.
        /// </summary>
        public bool SendMouse(float x, float y, MouseButton button, InputState state)
        {
            var mouse = new Mouse { Point = new PointF(x, y) };
            if (state == InputState.Pressed) mouse.Press(button);

            var e = new MouseButtonEvent
            {
                Mouse = mouse,
                Button = button,
                State = state,
                Modifiers = InputModifiers.None,
                Phase = EventPhase.Capturing,
            };
            Harness.Input.SendMouseButtonEvent(ref e);
            return e.IsConsumed;
        }

        public void Dispose() => Harness.Dispose();
    }

    /// <summary>A shell that records what was written to it and can be moved between states a real
    /// one only reaches by timing.</summary>
    sealed class Shell : ITerminalInput
    {
        readonly List<byte> _written = [];

        public static Shell Live() => new() { IsAcceptingInput = true };

        public static Shell NotStarted() => new() { IsAcceptingInput = false };

        public bool IsAcceptingInput { get; set; }

        public TerminalModes Modes { get; set; } = new(
            ApplicationCursorKeys: false,
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

        /// <summary>How many times bytes were handed over, not how many bytes — a doubled dispatch
        /// shows up here even when the sequence is one the shell would tolerate twice.</summary>
        public int Writes { get; private set; }

        public byte[] Written => _written.ToArray();

        public string Text => Encoding.Latin1.GetString(_written.ToArray());

        public void SendInput(ReadOnlySpan<byte> bytes)
        {
            Writes++;
            foreach (var b in bytes) _written.Add(b);
        }
    }

    /// <summary>Stands in for the application's keybinding controller, recording what reached it.</summary>
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
/// The view model's end of the input contract at the edges: what it answers before a shell exists,
/// and what it does with bytes once one no longer does.
/// </summary>
public class TerminalViewModelInputEdgeTests
{
    [Fact]
    public void WithNoShellYet_TheModesAreReadableRatherThanAbsent()
    {
        // The controller reads Modes on every keystroke, including the ones that arrive while the
        // shell is still spawning. Reaching through a null session here is a crash on the first key.
        var dispatcher = new ImmediateDispatcher();
        using var vm = new TerminalViewModel(new NeverStarts(), dispatcher);

        Assert.False(vm.Modes.ApplicationCursorKeys);
    }

    [Fact]
    public void SendingAnEmptySpan_IsANoOpRatherThanAThrow()
    {
        // The encoder returns 0 for every key the text pipeline carries, and a caller that slices by
        // that count hands over an empty span for each of them.
        var dispatcher = new ImmediateDispatcher();
        using var vm = new TerminalViewModel(new NeverStarts(), dispatcher);

        vm.SendInput(ReadOnlySpan<byte>.Empty);
    }

    [Fact]
    public void DisposingTwice_IsNotAnError()
    {
        // The pane is kept alive by the mode switcher and disposed by the window; both ends can run.
        var vm = new TerminalViewModel(new NeverStarts(), new ImmediateDispatcher());

        vm.Dispose();
        vm.Dispose();

        Assert.False(vm.IsAcceptingInput);
    }

    [Fact]
    public void SendingInputAfterDisposal_IsANoOpRatherThanAThrow()
    {
        // A keystroke already in the OS queue when the window closes arrives after this.
        var vm = new TerminalViewModel(new NeverStarts(), new ImmediateDispatcher());
        vm.Dispose();

        vm.SendInput("q"u8);
    }

    /// <summary>A launch that is never asked to start, because no viewport is ever reported.</summary>
    sealed class NeverStarts : ITerminalLaunch
    {
        public TerminalSize SizeFor(TerminalSize viewport) => viewport;

        public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher) =>
            throw new InvalidOperationException("This launch is never started.");
    }

    sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}
