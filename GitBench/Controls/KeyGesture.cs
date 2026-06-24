using System.Text;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Controls;

/// <summary>
/// A keyboard shortcut: a key plus optional modifiers. The single source for both a row action's
/// menu hint (<see cref="Display"/>) and the key its list controller dispatches on
/// (<see cref="Matches"/>), so the two can never drift.
/// </summary>
public readonly record struct KeyGesture(KeyboardKey Key, InputModifiers Modifiers = InputModifiers.None)
{
    // Lock/toggle state (CapsLock/NumLock) isn't part of a shortcut; only these gate a match, so a
    // bare-letter gesture still fires with CapsLock on but not under Ctrl/Alt/Super.
    private const InputModifiers RelevantMask =
        InputModifiers.Shift | InputModifiers.Control | InputModifiers.Alt | InputModifiers.Super;

    public bool Matches(KeyboardKey key, InputModifiers modifiers) =>
        key == Key && (modifiers & RelevantMask) == Modifiers;

    /// <summary>Menu hint text, e.g. "C", "Ctrl+C", "Enter", "Del".</summary>
    public string Display
    {
        get
        {
            var key = KeyLabel(Key);
            if (Modifiers == InputModifiers.None) return key;

            var sb = new StringBuilder();
            if ((Modifiers & InputModifiers.Control) != 0) sb.Append("Ctrl+");
            if ((Modifiers & InputModifiers.Alt) != 0) sb.Append("Alt+");
            if ((Modifiers & InputModifiers.Shift) != 0) sb.Append("Shift+");
            if ((Modifiers & InputModifiers.Super) != 0) sb.Append("Super+");
            sb.Append(key);
            return sb.ToString();
        }
    }

    private static string KeyLabel(KeyboardKey key) => key switch
    {
        KeyboardKey.Enter or KeyboardKey.NumpadEnter => "Enter",
        KeyboardKey.Delete => "Del",
        _ => key.ToString(),
    };
}
