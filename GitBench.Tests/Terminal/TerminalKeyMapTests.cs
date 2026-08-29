using GitBench.Features.Terminal;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The table joining physical keys to the keys the encoder has sequences for, swept rather than
/// sampled.
/// </summary>
/// <remarks>
/// The encoder's own table is swept the same way. Without this one the two tables are only ever
/// exercised at the keys some test happens to press, and a mapping with an arm missing is a shell
/// with no history recall, no Ctrl+D and nine dead function keys — all of it green.
/// </remarks>
public class TerminalKeyMapTests
{
    [Fact]
    public void EveryMappedPosition_StandsForTheTerminalKeyOfTheSameName()
    {
        // Every arm's destination, not just the count of them. Sweeping for "onto" and "one-to-one"
        // together characterises a permutation, which is exactly what a copy-paste slip leaves
        // behind: rotate F6..F10 by one and both of those still pass while every function key sends
        // its neighbour's sequence. Enter and NumpadEnter share a name deliberately; every other
        // position either has a terminal key of its own name or has none.
        var wrong = Enum.GetValues<KeyboardKey>()
            .Select(k => (Physical: k, Terminal: TerminalKeyMap.From(k)))
            .Where(p => p.Terminal != TerminalKey.None)
            .Where(p => p.Terminal.ToString()
                != p.Physical.ToString().Replace("Arrow", string.Empty).Replace("Numpad", string.Empty))
            .Select(p => $"{p.Physical} -> {p.Terminal}")
            .ToArray();

        Assert.Empty(wrong);
    }

    [Fact]
    public void EveryKeyTheEncoderHasASequenceFor_IsReachableFromSomePhysicalKey()
    {
        // The name rule above is silent about an arm that is simply missing, which is the other way
        // this table fails.
        var reachable = Enum.GetValues<KeyboardKey>().Select(TerminalKeyMap.From).ToHashSet();

        var unreachable = Enum.GetValues<TerminalKey>()
            .Where(k => k != TerminalKey.None && !reachable.Contains(k))
            .ToArray();

        Assert.Empty(unreachable);
    }

    [Theory]
    [InlineData(KeyboardKey.Alpha1)]
    [InlineData(KeyboardKey.Numpad1)]
    [InlineData(KeyboardKey.Slash)]
    [InlineData(KeyboardKey.Menu)]
    [InlineData(KeyboardKey.F13)]
    [InlineData(KeyboardKey.CapsLock)]
    public void APositionTheTerminalHasNoSequenceFor_MapsToNothing(KeyboardKey key) =>
        Assert.Equal(TerminalKey.None, TerminalKeyMap.From(key));

    // ---- which positions the operating system will turn into a character ----

    [Theory]
    [InlineData(KeyboardKey.A)]
    [InlineData(KeyboardKey.Alpha1)]
    [InlineData(KeyboardKey.Slash)]
    [InlineData(KeyboardKey.Space)]
    [InlineData(KeyboardKey.GraveAccent)]
    [InlineData(KeyboardKey.NumpadDecimal)]
    public void APositionThatTypes_SaysSo(KeyboardKey key) =>
        Assert.True(TerminalKeyMap.CanTypeACharacter(key));

    [Theory]
    [InlineData(KeyboardKey.LeftShift)]
    [InlineData(KeyboardKey.RightShift)]
    [InlineData(KeyboardKey.LeftControl)]
    [InlineData(KeyboardKey.RightControl)]
    [InlineData(KeyboardKey.LeftAlt)]
    [InlineData(KeyboardKey.RightAlt)]
    [InlineData(KeyboardKey.LeftSuper)]
    [InlineData(KeyboardKey.RightSuper)]
    [InlineData(KeyboardKey.CapsLock)]
    [InlineData(KeyboardKey.NumLock)]
    [InlineData(KeyboardKey.ScrollLock)]
    [InlineData(KeyboardKey.PrintScreen)]
    [InlineData(KeyboardKey.Pause)]
    [InlineData(KeyboardKey.Menu)]
    [InlineData(KeyboardKey.F13)]
    [InlineData(KeyboardKey.Unknown)]
    public void APositionThatNeverProducesACharacter_SaysSo(KeyboardKey key)
    {
        // Not the same list as "is a modifier". A pane that claims one of these as text stops the
        // keystroke dead: propagation ends and no character ever arrives to justify it.
        Assert.False(TerminalKeyMap.CanTypeACharacter(key));
    }

    // ---- modifiers ----

    [Theory]
    [InlineData(InputModifiers.None, TerminalKeyModifiers.None)]
    [InlineData(InputModifiers.Shift, TerminalKeyModifiers.Shift)]
    [InlineData(InputModifiers.Control, TerminalKeyModifiers.Ctrl)]
    [InlineData(InputModifiers.Alt, TerminalKeyModifiers.Alt)]
    [InlineData(InputModifiers.Control | InputModifiers.Alt, TerminalKeyModifiers.Ctrl | TerminalKeyModifiers.Alt)]
    public void TheChordingModifiers_CarryOver(InputModifiers held, TerminalKeyModifiers expected) =>
        Assert.Equal(expected, TerminalKeyMap.From(held));

    [Theory]
    [InlineData(InputModifiers.NumLock)]
    [InlineData(InputModifiers.CapsLock)]
    [InlineData(InputModifiers.NumLock | InputModifiers.CapsLock)]
    [InlineData(InputModifiers.Super)]
    public void ALockKeyOrSuper_IsNotATerminalModifier(InputModifiers held) =>
        Assert.Equal(TerminalKeyModifiers.None, TerminalKeyMap.From(held));

    [Fact]
    public void ALockKeyRidingAlongWithARealModifier_DoesNotJoinIt() =>
        Assert.Equal(
            TerminalKeyModifiers.Ctrl,
            TerminalKeyMap.From(InputModifiers.Control | InputModifiers.NumLock | InputModifiers.CapsLock));
}
