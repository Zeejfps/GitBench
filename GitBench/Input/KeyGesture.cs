using System.Text;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Input;

/// <summary>
/// A keyboard shortcut: a key plus optional modifiers. The single source for both a shortcut's
/// on-screen hint (<see cref="Display"/>) and the key a handler dispatches on (<see cref="Matches"/>),
/// so the two can never drift.
/// </summary>
public readonly record struct KeyGesture(KeyboardKey Key, InputModifiers Modifiers = InputModifiers.None)
{
    /// <summary>
    /// The modifiers that take part in a match. Lock/toggle state (CapsLock/NumLock) isn't part of a
    /// shortcut, so a bare-letter gesture still fires with CapsLock on but not under Ctrl/Alt/Super.
    /// </summary>
    public const InputModifiers RelevantMask =
        InputModifiers.Shift | InputModifiers.Control | InputModifiers.Alt | InputModifiers.Super;

    /// <summary>The platform's shortcut modifier: Cmd on macOS, Ctrl everywhere else.</summary>
    public static readonly InputModifiers Primary =
        OperatingSystem.IsMacOS() ? InputModifiers.Super : InputModifiers.Control;

    /// <summary>A chord under <see cref="Primary"/>, optionally with further modifiers.</summary>
    public static KeyGesture WithPrimary(KeyboardKey key, InputModifiers also = InputModifiers.None) =>
        new(key, Primary | also);

    public bool Matches(KeyboardKey key, InputModifiers modifiers) =>
        key == Key && (modifiers & RelevantMask) == Modifiers;

    /// <summary>
    /// Whether the key is a modifier on its own (a Shift, Ctrl, Alt or Super key), which no gesture
    /// is built on: they only ever qualify another key.
    /// </summary>
    public static bool IsModifierKey(KeyboardKey key) => key is
        KeyboardKey.LeftShift or KeyboardKey.RightShift
        or KeyboardKey.LeftControl or KeyboardKey.RightControl
        or KeyboardKey.LeftAlt or KeyboardKey.RightAlt
        or KeyboardKey.LeftSuper or KeyboardKey.RightSuper;

    /// <summary>
    /// The stored form, e.g. "Control+Shift+F12": modifier names in a fixed order, then the key's
    /// name. Platform-neutral on purpose — <see cref="Display"/> is what changes per OS, this does not.
    /// </summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        if ((Modifiers & InputModifiers.Control) != 0) sb.Append("Control+");
        if ((Modifiers & InputModifiers.Alt) != 0) sb.Append("Alt+");
        if ((Modifiers & InputModifiers.Shift) != 0) sb.Append("Shift+");
        if ((Modifiers & InputModifiers.Super) != 0) sb.Append("Super+");
        sb.Append(Key.ToString());
        return sb.ToString();
    }

    /// <summary>
    /// Reads a <see cref="Serialize"/>d gesture. Refuses anything that isn't a known key with known
    /// modifiers — a hand-edited file or a key removed in a later version yields no gesture rather
    /// than a wrong one.
    /// </summary>
    public static bool TryParse(string? text, out KeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = InputModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "control": modifiers |= InputModifiers.Control; break;
                case "alt": modifiers |= InputModifiers.Alt; break;
                case "shift": modifiers |= InputModifiers.Shift; break;
                case "super": modifiers |= InputModifiers.Super; break;
                default: return false;
            }
        }

        if (!Enum.TryParse<KeyboardKey>(parts[^1], ignoreCase: true, out var key)
            || !Enum.IsDefined(key)
            || key == KeyboardKey.Unknown
            || IsModifierKey(key))
            return false;

        gesture = new KeyGesture(key, modifiers);
        return true;
    }

    /// <summary>Hint text, e.g. "C", "Ctrl+C", "Enter", "Del", "?"; Super reads as the command glyph on macOS.</summary>
    public string Display
    {
        get
        {
            if (Modifiers == InputModifiers.Shift && Key == KeyboardKey.Slash) return "?";

            var key = KeyLabel(Key);
            if (Modifiers == InputModifiers.None) return key;

            var sb = new StringBuilder();
            if ((Modifiers & InputModifiers.Control) != 0) sb.Append("Ctrl+");
            if ((Modifiers & InputModifiers.Alt) != 0) sb.Append("Alt+");
            if ((Modifiers & InputModifiers.Shift) != 0) sb.Append("Shift+");
            if ((Modifiers & InputModifiers.Super) != 0) sb.Append(OperatingSystem.IsMacOS() ? "⌘" : "Super+");
            sb.Append(key);
            return sb.ToString();
        }
    }

    private static string KeyLabel(KeyboardKey key) => key switch
    {
        KeyboardKey.Enter or KeyboardKey.NumpadEnter => "Enter",
        KeyboardKey.Delete => "Del",
        KeyboardKey.Escape => "Esc",
        KeyboardKey.Space => "Space",
        KeyboardKey.Slash => "/",
        KeyboardKey.LeftBracket => "[",
        KeyboardKey.RightBracket => "]",
        KeyboardKey.UpArrow => "↑",
        KeyboardKey.DownArrow => "↓",
        KeyboardKey.LeftArrow => "←",
        KeyboardKey.RightArrow => "→",
        KeyboardKey.PageUp => "PgUp",
        KeyboardKey.PageDown => "PgDn",
        >= KeyboardKey.Alpha0 and <= KeyboardKey.Alpha9 => ((char)('0' + (key - KeyboardKey.Alpha0))).ToString(),
        >= KeyboardKey.Numpad0 and <= KeyboardKey.Numpad9 => "Num " + (key - KeyboardKey.Numpad0),
        _ => key.ToString(),
    };
}
