using GitBench.Input;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using Xunit;

namespace GitBench.Tests;

/// <summary>Rebinding: what changes, what the map still answers, and what is worth storing.</summary>
public class KeyBindingsTests
{
    private static readonly KeyGesture CtrlShiftR = new(KeyboardKey.R, InputModifiers.Control | InputModifiers.Shift);

    [Theory]
    [InlineData(KeyboardKey.F5, InputModifiers.None, "F5")]
    [InlineData(KeyboardKey.B, InputModifiers.Control, "Control+B")]
    [InlineData(KeyboardKey.F12, InputModifiers.Shift, "Shift+F12")]
    [InlineData(KeyboardKey.C, InputModifiers.Control | InputModifiers.Shift, "Control+Shift+C")]
    [InlineData(KeyboardKey.K, InputModifiers.Super, "Super+K")]
    [InlineData(KeyboardKey.Slash, InputModifiers.Control | InputModifiers.Alt | InputModifiers.Shift | InputModifiers.Super, "Control+Alt+Shift+Super+Slash")]
    public void AGestureRoundTripsThroughItsStoredForm(KeyboardKey key, InputModifiers modifiers, string stored)
    {
        var gesture = new KeyGesture(key, modifiers);

        Assert.Equal(stored, gesture.Serialize());
        Assert.True(KeyGesture.TryParse(stored, out var parsed));
        Assert.Equal(gesture, parsed);
    }

    [Fact]
    public void ParsingIgnoresCaseAndSpacing()
    {
        Assert.True(KeyGesture.TryParse(" control + shift + r ", out var parsed));
        Assert.Equal(CtrlShiftR, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Control+")]
    [InlineData("Hyper+B")]
    [InlineData("NoSuchKey")]
    [InlineData("Unknown")]
    [InlineData("LeftShift")]
    [InlineData("Control+LeftControl")]
    public void AStoredFormThatIsNotAGesture_IsRefused(string stored)
    {
        Assert.False(KeyGesture.TryParse(stored, out _));
    }

    [Fact]
    public void RebindingReplacesEveryGestureTheCommandHad()
    {
        var keys = new KeyMap();

        keys.Rebind(KeyCommand.ListActivate, CtrlShiftR);

        Assert.Equal([CtrlShiftR], keys.GesturesFor(KeyCommand.ListActivate));
        Assert.True(keys.Matches(KeyCommand.ListActivate, KeyboardKey.R, InputModifiers.Control | InputModifiers.Shift));
        Assert.False(keys.Matches(KeyCommand.ListActivate, KeyboardKey.Enter, InputModifiers.None));
        Assert.False(keys.Matches(KeyCommand.ListActivate, KeyboardKey.NumpadEnter, InputModifiers.None));
        Assert.Equal("Ctrl+Shift+R", keys.Display(KeyCommand.ListActivate));
    }

    [Fact]
    public void ARebindLeavesEveryOtherCommandAlone()
    {
        var keys = new KeyMap();
        var before = Enum.GetValues<KeyCommand>().ToDictionary(c => c, c => keys.GesturesFor(c).ToArray());

        keys.Rebind(KeyCommand.Refresh, CtrlShiftR);

        foreach (var command in Enum.GetValues<KeyCommand>().Where(c => c != KeyCommand.Refresh))
            Assert.Equal(before[command], keys.GesturesFor(command));
    }

    [Fact]
    public void IsDefaultFollowsTheRebindAndTheReset()
    {
        var keys = new KeyMap();
        Assert.True(keys.IsDefault(KeyCommand.Refresh));

        keys.Rebind(KeyCommand.Refresh, CtrlShiftR);
        Assert.False(keys.IsDefault(KeyCommand.Refresh));

        keys.Reset(KeyCommand.Refresh);
        Assert.True(keys.IsDefault(KeyCommand.Refresh));
        Assert.Equal(keys.DefaultsFor(KeyCommand.Refresh), keys.GesturesFor(KeyCommand.Refresh));
    }

    [Fact]
    public void RebindingToTheDefaultGestureCountsAsDefault()
    {
        var keys = new KeyMap();

        keys.Rebind(KeyCommand.Refresh, new KeyGesture(KeyboardKey.F5));

        Assert.True(keys.IsDefault(KeyCommand.Refresh));
        Assert.Empty(keys.Overrides);
    }

    [Fact]
    public void OverridesAreOnlyTheCommandsOffTheirDefaults()
    {
        var keys = new KeyMap();
        keys.Rebind(KeyCommand.Refresh, CtrlShiftR);
        keys.Rebind(KeyCommand.SaveFile, new KeyGesture(KeyboardKey.F2));

        var overrides = keys.Overrides;

        Assert.Equal([KeyCommand.Refresh, KeyCommand.SaveFile], overrides.Select(o => o.Command));
        Assert.Equal([CtrlShiftR], overrides[0].Gestures);
    }

    [Fact]
    public void AMapBuiltFromOverridesStartsWhereTheLastOneLeftOff()
    {
        var first = new KeyMap();
        first.Rebind(KeyCommand.Refresh, CtrlShiftR);

        var second = new KeyMap(first.Overrides);

        Assert.Equal([CtrlShiftR], second.GesturesFor(KeyCommand.Refresh));
        Assert.False(second.IsDefault(KeyCommand.Refresh));
        Assert.True(second.IsDefault(KeyCommand.SaveFile));
    }

    [Fact]
    public void AnOverrideWithNoGestures_LeavesTheDefault()
    {
        var keys = new KeyMap([new KeyBinding(KeyCommand.Refresh, [])]);

        Assert.True(keys.IsDefault(KeyCommand.Refresh));
    }

    [Fact]
    public void ResetAllPutsEveryCommandBack()
    {
        var keys = new KeyMap();
        keys.Rebind(KeyCommand.Refresh, CtrlShiftR);
        keys.Rebind(KeyCommand.SaveFile, new KeyGesture(KeyboardKey.F2));

        keys.ResetAll();

        Assert.Empty(keys.Overrides);
        foreach (var command in Enum.GetValues<KeyCommand>())
            Assert.True(keys.IsDefault(command));
    }

    [Fact]
    public void TheVersionBumpsOnlyWhenSomethingChanges()
    {
        var keys = new KeyMap();
        var start = keys.Version.Value;

        keys.Reset(KeyCommand.Refresh);
        keys.ResetAll();
        Assert.Equal(start, keys.Version.Value);

        keys.Rebind(KeyCommand.Refresh, CtrlShiftR);
        Assert.Equal(start + 1, keys.Version.Value);

        keys.Rebind(KeyCommand.Refresh, CtrlShiftR);
        Assert.Equal(start + 1, keys.Version.Value);

        keys.Reset(KeyCommand.Refresh);
        Assert.Equal(start + 2, keys.Version.Value);
    }

    [Fact]
    public void TheSharedDefaultsCannotBeEdited()
    {
        Assert.IsNotAssignableFrom<KeyMap>(KeyMap.Defaults);
    }

    [Fact]
    public void AConflictIsAnotherCommandOnTheSameSurface_OrAnAppWideOne()
    {
        var keys = new KeyMap();
        var j = new KeyGesture(KeyboardKey.J);

        // Review's own J: a conflict for a review command.
        Assert.Equal([KeyCommand.ReviewNextFile], keys.ConflictsWith(KeyCommand.ReviewPrevFile, j));
        // The same J on a list: a different surface, so not a conflict.
        Assert.Empty(keys.ConflictsWith(KeyCommand.ListUp, j));
        // An app-wide chord conflicts with anything, and anything conflicts with it.
        var f5 = new KeyGesture(KeyboardKey.F5);
        Assert.Equal([KeyCommand.Refresh], keys.ConflictsWith(KeyCommand.SaveFile, f5));
        Assert.Equal([KeyCommand.SaveFile], new KeyMap([new KeyBinding(KeyCommand.SaveFile, [f5])])
            .ConflictsWith(KeyCommand.Refresh, f5));
        // A command never conflicts with itself.
        Assert.Empty(keys.ConflictsWith(KeyCommand.Refresh, f5));
    }
}
