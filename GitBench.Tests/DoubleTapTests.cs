using GitBench.App;
using GitBench.Input;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using Xunit;

namespace GitBench.Tests;

/// <summary>What counts as a double tap of a modifier, on a clock the test moves.</summary>
public class DoubleTapDetectorTests
{
    private readonly ManualTimeProvider _clock = new();
    private readonly DoubleTapDetector _detector;
    private readonly List<TapModifier> _fired = new();

    public DoubleTapDetectorTests() => _detector = new DoubleTapDetector(_clock);

    private void Key(KeyboardKey key, InputState state)
    {
        var e = new KeyboardKeyEvent { Key = key, State = state, Modifiers = InputModifiers.None, Phase = EventPhase.Capturing };
        if (_detector.OnKey(in e) is { } modifier) _fired.Add(modifier);
    }

    private void Tap(KeyboardKey key = KeyboardKey.LeftShift, int heldMs = 50)
    {
        Key(key, InputState.Pressed);
        Wait(heldMs);
        Key(key, InputState.Released);
    }

    private void Wait(int ms) => _clock.Advance(TimeSpan.FromMilliseconds(ms));

    private void MouseDown()
    {
        var e = new MouseButtonEvent { Mouse = new Mouse(), Button = MouseButton.Left, State = InputState.Pressed, Phase = EventPhase.Capturing };
        _detector.OnMouseButton(in e);
    }

    [Fact]
    public void TwoQuickTaps_Fire()
    {
        Tap();
        Wait(100);
        Tap();

        Assert.Equal([TapModifier.Shift], _fired);
    }

    [Fact]
    public void EitherShiftKey_CountsAsTheSameModifier()
    {
        Tap(KeyboardKey.LeftShift);
        Wait(100);
        Tap(KeyboardKey.RightShift);

        Assert.Equal([TapModifier.Shift], _fired);
    }

    [Fact]
    public void ATapHeldTooLong_DoesNotCount()
    {
        Tap();
        Wait(100);
        Tap(heldMs: 350);

        Assert.Empty(_fired);
    }

    [Fact]
    public void TapsStartingTooFarApart_DoNotFire()
    {
        Tap();
        Wait(400);
        Tap();

        Assert.Empty(_fired);
    }

    [Fact]
    public void ASlowPairStillLeavesTheSecondTapToStartANewOne()
    {
        Tap();
        Wait(500);
        Tap();
        Wait(100);
        Tap();

        Assert.Equal([TapModifier.Shift], _fired);
    }

    [Fact]
    public void RepeatsWhileHeld_AreNotFurtherTaps()
    {
        Key(KeyboardKey.LeftShift, InputState.Pressed);
        Wait(40);
        Key(KeyboardKey.LeftShift, InputState.Pressed);
        Wait(40);
        Key(KeyboardKey.LeftShift, InputState.Pressed);
        Wait(40);
        Key(KeyboardKey.LeftShift, InputState.Released);

        Assert.Empty(_fired);
    }

    [Fact]
    public void HoldingShift_ThenTappingOnce_DoesNotFire()
    {
        Key(KeyboardKey.LeftShift, InputState.Pressed);
        for (var i = 0; i < 20; i++)
        {
            Wait(30);
            Key(KeyboardKey.LeftShift, InputState.Pressed);
        }
        Key(KeyboardKey.LeftShift, InputState.Released);
        Wait(100);
        Tap();

        Assert.Empty(_fired);
    }

    [Fact]
    public void ShiftUsedForACapital_SpoilsTheTap()
    {
        Tap();
        Wait(50);
        Key(KeyboardKey.LeftShift, InputState.Pressed);
        Key(KeyboardKey.A, InputState.Pressed);
        Key(KeyboardKey.A, InputState.Released);
        Key(KeyboardKey.LeftShift, InputState.Released);

        Assert.Empty(_fired);
    }

    [Fact]
    public void AKeyBetweenTheTaps_SpoilsThePair()
    {
        Tap();
        Key(KeyboardKey.A, InputState.Pressed);
        Key(KeyboardKey.A, InputState.Released);
        Tap();

        Assert.Empty(_fired);
    }

    [Fact]
    public void ShiftClick_SpoilsTheTap()
    {
        Tap();
        Wait(50);
        Key(KeyboardKey.LeftShift, InputState.Pressed);
        MouseDown();
        Key(KeyboardKey.LeftShift, InputState.Released);

        Assert.Empty(_fired);
    }

    [Fact]
    public void AnotherModifierJoiningIn_SpoilsTheTap()
    {
        Tap();
        Key(KeyboardKey.LeftShift, InputState.Pressed);
        Key(KeyboardKey.LeftControl, InputState.Pressed);
        Key(KeyboardKey.LeftControl, InputState.Released);
        Key(KeyboardKey.LeftShift, InputState.Released);

        Assert.Empty(_fired);
    }

    [Fact]
    public void AReleaseOfAKeyPressedBeforeTheTap_DoesNotSpoilIt()
    {
        Key(KeyboardKey.A, InputState.Pressed);
        Tap();
        Key(KeyboardKey.A, InputState.Released);
        Wait(100);
        Tap();

        Assert.Equal([TapModifier.Shift], _fired);
    }

    [Fact]
    public void LosingWindowFocus_ForgetsAHeldModifier()
    {
        Key(KeyboardKey.LeftShift, InputState.Pressed);
        _detector.Reset();
        Tap();
        Wait(100);
        Tap();

        Assert.Equal([TapModifier.Shift], _fired);
    }

    [Fact]
    public void ThreeTaps_FireOnce()
    {
        Tap();
        Wait(100);
        Tap();
        Wait(100);
        Tap();

        Assert.Single(_fired);
    }
}

/// <summary>Double taps as bindings: how they read, how they are stored, and how they collide.</summary>
public class DoubleTapBindingTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-doubletap-prefs-");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void SearchEverywhere_IsDoubleShift_ThenPrimaryT()
    {
        var triggers = new KeyMap().TriggersFor(KeyCommand.SearchEverywhere);

        Assert.Equal(new KeyTrigger.DoubleTap(TapModifier.Shift), triggers[0]);
        Assert.Equal(KeyGesture.WithPrimary(KeyboardKey.T), triggers[1]);
        Assert.Equal("Double Shift", new KeyMap().Display(KeyCommand.SearchEverywhere));
    }

    [Theory]
    [InlineData(TapModifier.Shift, "Double Shift")]
    [InlineData(TapModifier.Control, "Double Control")]
    [InlineData(TapModifier.Alt, "Double Alt")]
    public void ADoubleTapRoundTripsThroughItsStoredForm(TapModifier modifier, string stored)
    {
        var trigger = new KeyTrigger.DoubleTap(modifier);

        Assert.Equal(stored, trigger.Serialize());
        Assert.True(KeyTrigger.TryParse(stored, out var parsed));
        Assert.Equal(trigger, parsed);
    }

    [Theory]
    [InlineData("Double")]
    [InlineData("Double ")]
    [InlineData("Double Super")]
    [InlineData("Double F5")]
    public void AStoredDoubleTapOfNoModifier_IsRefused(string stored)
    {
        Assert.False(KeyTrigger.TryParse(stored, out _));
    }

    [Fact]
    public void AStrokeStillParsesAsAStroke()
    {
        Assert.True(KeyTrigger.TryParse("Control+Shift+R", out var parsed));
        Assert.Equal(new KeyTrigger.Stroke(new KeyGesture(KeyboardKey.R, InputModifiers.Control | InputModifiers.Shift)), parsed);
    }

    [Fact]
    public void ADoubleTapBindingRoundTripsThroughPreferences()
    {
        var path = Path.Combine(_dir.Path, "prefs.json");
        var saved = Preferences.Default with
        {
            KeyBindings = [new KeyBinding(KeyCommand.Refresh, [new KeyTrigger.DoubleTap(TapModifier.Control)])],
        };

        PreferencesStore.Save(path, saved);
        var loaded = PreferencesStore.Load(path);

        var binding = Assert.Single(loaded.KeyBindings);
        Assert.Equal(KeyCommand.Refresh, binding.Command);
        Assert.Equal([new KeyTrigger.DoubleTap(TapModifier.Control)], binding.Triggers);
        Assert.Contains("Double Control", File.ReadAllText(path));
    }

    [Fact]
    public void ADoubleTapIsMatchedOnlyAsADoubleTap()
    {
        var keys = new KeyMap();

        Assert.True(keys.MatchesDoubleTap(KeyCommand.SearchEverywhere, TapModifier.Shift));
        Assert.False(keys.MatchesDoubleTap(KeyCommand.SearchEverywhere, TapModifier.Control));
        Assert.False(keys.Matches(KeyCommand.SearchEverywhere, KeyboardKey.LeftShift, InputModifiers.Shift));
        Assert.False(keys.MatchesDoubleTap(KeyCommand.Refresh, TapModifier.Shift));
    }

    [Fact]
    public void NoDefaultBinding_ConflictsWithAnother()
    {
        var keys = new KeyMap();
        var conflicts =
            from command in Enum.GetValues<KeyCommand>()
            from trigger in keys.TriggersFor(command)
            from other in keys.ConflictsWith(command, trigger)
            select $"{command} {trigger.Display} / {other}";

        Assert.Empty(conflicts);
    }

    [Fact]
    public void TheSameDoubleTapOnTwoAppWideCommands_IsAConflict()
    {
        var keys = new KeyMap();
        keys.Rebind(KeyCommand.Refresh, new KeyTrigger.DoubleTap(TapModifier.Shift));

        Assert.Equal([KeyCommand.SearchEverywhere], keys.ConflictsWith(KeyCommand.Refresh, new KeyTrigger.DoubleTap(TapModifier.Shift)));
        Assert.Empty(keys.ConflictsWith(KeyCommand.Refresh, new KeyTrigger.DoubleTap(TapModifier.Alt)));
    }
}

/// <summary>The recognizer in front of the focus path: a double tap runs its command, and no key is claimed.</summary>
public class KeySequenceRecognizerTests
{
    [Fact]
    public void ADoubleTap_RunsTheCommandBoundToIt_AndClaimsNoKey()
    {
        var clock = new ManualTimeProvider();
        var ran = new List<KeyCommand>();
        var recognizer = new KeySequenceRecognizer(new KeyMap(), clock, ran.Add);
        var claimed = false;

        void Send(InputState state)
        {
            var e = new KeyboardKeyEvent { Key = KeyboardKey.LeftShift, State = state, Modifiers = InputModifiers.Shift, Phase = EventPhase.Capturing };
            recognizer.OnKey(ref e);
            claimed |= e.IsConsumed;
        }

        Send(InputState.Pressed);
        Send(InputState.Released);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Send(InputState.Pressed);
        Send(InputState.Released);

        Assert.Equal([KeyCommand.SearchEverywhere], ran);
        Assert.False(claimed);
    }
}
