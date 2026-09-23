using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Input;

/// <summary>
/// Which keys run which <see cref="KeyCommand"/>. The one place a binding is decided: handlers
/// match through it, and anything that prints a shortcut reads the same table.
/// </summary>
public interface IKeyMap
{
    /// <summary>Every trigger bound to the command, the primary one first. Never empty.</summary>
    IReadOnlyList<KeyTrigger> TriggersFor(KeyCommand command);

    /// <summary>Whether a key press, with these modifiers, is one of the command's strokes.</summary>
    bool Matches(KeyCommand command, KeyboardKey key, InputModifiers modifiers);

    /// <summary>Whether a double tap of the modifier is one of the command's triggers.</summary>
    bool MatchesDoubleTap(KeyCommand command, TapModifier modifier);

    /// <summary>Hint text for the command's primary trigger, e.g. "Ctrl+B" or "Double Shift".</summary>
    string Display(KeyCommand command);
}

/// <summary>A command's triggers as the user set them, for carrying between the key map and storage.</summary>
public sealed record KeyBinding(KeyCommand Command, IReadOnlyList<KeyTrigger> Triggers);

public static class KeyMapContext
{
    /// <summary>
    /// The keymap in scope. The immutable defaults answer where none was registered, so a surface
    /// built outside the app's wiring still has its keys.
    /// </summary>
    public static IKeyMap KeyMap(this Context ctx) => ctx.Get<IKeyMap>() ?? Input.KeyMap.Defaults;
}
