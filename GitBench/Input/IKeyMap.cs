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
    /// <summary>Every gesture bound to the command, the primary one first. Never empty.</summary>
    IReadOnlyList<KeyGesture> GesturesFor(KeyCommand command);

    /// <summary>Whether a key press, with these modifiers, is one of the command's gestures.</summary>
    bool Matches(KeyCommand command, KeyboardKey key, InputModifiers modifiers);

    /// <summary>Hint text for the command's primary gesture, e.g. "Ctrl+B".</summary>
    string Display(KeyCommand command);
}

public static class KeyMapContext
{
    /// <summary>
    /// The keymap in scope. The immutable defaults answer where none was registered, so a surface
    /// built outside the app's wiring still has its keys.
    /// </summary>
    public static IKeyMap KeyMap(this Context ctx) => ctx.Get<IKeyMap>() ?? Input.KeyMap.Defaults;
}
