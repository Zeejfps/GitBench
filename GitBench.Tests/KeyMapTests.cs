using GitBench.Input;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using Xunit;

namespace GitBench.Tests;

public class KeyMapTests
{
    private readonly KeyMap _keys = new();

    [Fact]
    public void EveryCommandHasAtLeastOneDefaultGesture()
    {
        foreach (var command in Enum.GetValues<KeyCommand>())
            Assert.NotEmpty(_keys.GesturesFor(command));
    }

    [Fact]
    public void DisplayIsThePrimaryGestures()
    {
        foreach (var command in Enum.GetValues<KeyCommand>())
            Assert.Equal(_keys.GesturesFor(command)[0].Display, _keys.Display(command));
    }

    [Fact]
    public void LockKeysDoNotAffectAMatch()
    {
        Assert.True(_keys.Matches(KeyCommand.Refresh, KeyboardKey.F5, InputModifiers.CapsLock | InputModifiers.NumLock));
        Assert.True(_keys.Matches(KeyCommand.ToggleRepoBar, KeyboardKey.B, KeyGesture.Primary | InputModifiers.NumLock));
    }

    [Fact]
    public void AnExtraModifierBreaksAMatch()
    {
        Assert.False(_keys.Matches(KeyCommand.Refresh, KeyboardKey.F5, InputModifiers.Control));
        Assert.False(_keys.Matches(KeyCommand.ReviewNextFile, KeyboardKey.J, InputModifiers.Control));
        Assert.False(_keys.Matches(KeyCommand.ToggleRepoBar, KeyboardKey.B, KeyGesture.Primary | InputModifiers.Shift));
    }

    [Fact]
    public void AlternateGesturesMatchToo()
    {
        Assert.True(_keys.Matches(KeyCommand.ListActivate, KeyboardKey.Enter, InputModifiers.None));
        Assert.True(_keys.Matches(KeyCommand.ListActivate, KeyboardKey.NumpadEnter, InputModifiers.None));
        Assert.True(_keys.Matches(KeyCommand.RepoHotkey3, KeyboardKey.Alpha3, KeyGesture.Primary));
        Assert.True(_keys.Matches(KeyCommand.RepoHotkey3, KeyboardKey.Numpad3, KeyGesture.Primary));
    }

    [Fact]
    public void RepoHotkeySlotsFollowTheCommandOrder()
    {
        for (var slot = 1; slot <= 9; slot++)
            Assert.Equal(slot, KeyCommands.RepoHotkeySlot(KeyCommands.RepoHotkeys[slot - 1]));
        Assert.Null(KeyCommands.RepoHotkeySlot(KeyCommand.Refresh));
        Assert.Null(KeyCommands.RepoHotkeySlot(KeyCommand.ListUp));
    }

    [Theory]
    [InlineData(KeyboardKey.Enter, InputModifiers.None, "Enter")]
    [InlineData(KeyboardKey.NumpadEnter, InputModifiers.None, "Enter")]
    [InlineData(KeyboardKey.Delete, InputModifiers.None, "Del")]
    [InlineData(KeyboardKey.Escape, InputModifiers.None, "Esc")]
    [InlineData(KeyboardKey.Space, InputModifiers.None, "Space")]
    [InlineData(KeyboardKey.Slash, InputModifiers.Shift, "?")]
    [InlineData(KeyboardKey.Alpha1, InputModifiers.None, "1")]
    [InlineData(KeyboardKey.J, InputModifiers.None, "J")]
    [InlineData(KeyboardKey.C, InputModifiers.Control | InputModifiers.Shift, "Ctrl+Shift+C")]
    public void DisplayNamesKeysTheWayACapDoes(KeyboardKey key, InputModifiers modifiers, string expected) =>
        Assert.Equal(expected, new KeyGesture(key, modifiers).Display);

    [Fact]
    public void PrimaryChordDisplaysAsThePlatformModifier()
    {
        var expected = OperatingSystem.IsMacOS() ? "⌘B" : "Ctrl+B";
        Assert.Equal(expected, _keys.Display(KeyCommand.ToggleRepoBar));
    }
}
