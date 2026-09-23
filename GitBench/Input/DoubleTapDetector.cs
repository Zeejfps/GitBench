using ZGF.Gui.Desktop.Input;

namespace GitBench.Input;

/// <summary>
/// Recognises a modifier tapped twice on its own: pressed and released within
/// <see cref="TapLimit"/>, twice, the two taps starting within <see cref="PairLimit"/> of each
/// other. Any other key or a mouse button pressed in between spoils it, so Shift used for a capital
/// or held for a Shift-click never counts. Only watches: the modifier still works as one for
/// whatever has focus.
/// </summary>
internal sealed class DoubleTapDetector
{
    public static readonly TimeSpan TapLimit = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan PairLimit = TimeSpan.FromMilliseconds(400);

    private readonly TimeProvider _time;
    private Held? _held;
    private Tap? _lastTap;

    public DoubleTapDetector(TimeProvider time) => _time = time;

    private sealed record Held(TapModifier Modifier, DateTimeOffset Since, bool Spoiled);

    private readonly record struct Tap(TapModifier Modifier, DateTimeOffset Started);

    /// <summary>Takes one key event; answers the modifier when it completes a double tap.</summary>
    public TapModifier? OnKey(in KeyboardKeyEvent e)
    {
        if (TapModifiers.Of(e.Key) is not { } modifier)
        {
            if (e.State == InputState.Pressed) Spoil();
            return null;
        }

        if (e.State == InputState.Pressed)
        {
            Press(modifier);
            return null;
        }

        return Release(modifier);
    }

    public void OnMouseButton(in MouseButtonEvent e)
    {
        if (e.State == InputState.Pressed) Spoil();
    }

    public void Reset()
    {
        _held = null;
        _lastTap = null;
    }

    private void Press(TapModifier modifier)
    {
        // A held key repeats as further presses.
        if (_held is { } held && held.Modifier == modifier) return;

        if (_held is not null)
        {
            Spoil();
            return;
        }

        _held = new Held(modifier, _time.GetUtcNow(), Spoiled: false);
    }

    private TapModifier? Release(TapModifier modifier)
    {
        if (_held is not { } held || held.Modifier != modifier)
        {
            _held = null;
            _lastTap = null;
            return null;
        }

        _held = null;
        if (held.Spoiled || _time.GetUtcNow() - held.Since > TapLimit)
        {
            _lastTap = null;
            return null;
        }

        if (_lastTap is { } previous && previous.Modifier == modifier && held.Since - previous.Started <= PairLimit)
        {
            _lastTap = null;
            return modifier;
        }

        _lastTap = new Tap(modifier, held.Since);
        return null;
    }

    private void Spoil()
    {
        if (_held is { } held) _held = held with { Spoiled = true };
        _lastTap = null;
    }
}
