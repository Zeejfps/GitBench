using System.Diagnostics.CodeAnalysis;
using ZGF.KeyboardModule;

namespace GitBench.Input;

/// <summary>
/// What sets a <see cref="KeyCommand"/> off: a key stroke, or a modifier tapped twice on its own.
/// </summary>
public abstract record KeyTrigger
{
    private KeyTrigger() { }

    /// <summary>A key, with modifiers held.</summary>
    public sealed record Stroke(KeyGesture Gesture) : KeyTrigger;

    /// <summary>A modifier pressed and released twice in quick succession, with nothing in between.</summary>
    public sealed record DoubleTap(TapModifier Modifier) : KeyTrigger;

    public static implicit operator KeyTrigger(KeyGesture gesture) => new Stroke(gesture);

    private const string DoublePrefix = "Double ";

    /// <summary>Hint text, e.g. "Ctrl+B" or "Double Shift".</summary>
    public string Display => this switch
    {
        Stroke stroke => stroke.Gesture.Display,
        DoubleTap tap => DoublePrefix + TapModifiers.Display(tap.Modifier),
        _ => throw new InvalidOperationException($"No display for {GetType().Name}."),
    };

    /// <summary>The stored form: a <see cref="KeyGesture.Serialize"/>d stroke, or "Double Shift".</summary>
    public string Serialize() => this switch
    {
        Stroke stroke => stroke.Gesture.Serialize(),
        DoubleTap tap => DoublePrefix + tap.Modifier,
        _ => throw new InvalidOperationException($"No stored form for {GetType().Name}."),
    };

    /// <summary>Reads a <see cref="Serialize"/>d trigger, refusing anything it does not recognise whole.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out KeyTrigger? trigger)
    {
        trigger = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith(DoublePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<TapModifier>(trimmed[DoublePrefix.Length..].Trim(), ignoreCase: true, out var modifier)
                || !Enum.IsDefined(modifier))
                return false;
            trigger = new DoubleTap(modifier);
            return true;
        }

        if (!KeyGesture.TryParse(trimmed, out var gesture)) return false;
        trigger = new Stroke(gesture);
        return true;
    }
}

/// <summary>A modifier that can be tapped on its own, either of its keys counting as the same one.</summary>
public enum TapModifier
{
    Shift,
    Control,
    Alt,
}

public static class TapModifiers
{
    /// <summary>The modifier a key is one side of, or null for any other key.</summary>
    public static TapModifier? Of(KeyboardKey key) => key switch
    {
        KeyboardKey.LeftShift or KeyboardKey.RightShift => TapModifier.Shift,
        KeyboardKey.LeftControl or KeyboardKey.RightControl => TapModifier.Control,
        KeyboardKey.LeftAlt or KeyboardKey.RightAlt => TapModifier.Alt,
        _ => null,
    };

    public static string Display(TapModifier modifier) => modifier switch
    {
        TapModifier.Shift => "Shift",
        TapModifier.Control => "Ctrl",
        TapModifier.Alt => "Alt",
        _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, "No display."),
    };
}
