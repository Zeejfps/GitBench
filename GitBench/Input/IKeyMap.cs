using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using ZGF.Observable;

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

/// <summary>A command's gestures as the user set them, for carrying between the key map and storage.</summary>
public sealed record KeyBinding(KeyCommand Command, IReadOnlyList<KeyGesture> Gestures);

/// <summary>
/// The editable side of the key map: what the user has changed, and how to change it. A rebind
/// takes effect at once for every handler that matches through the same map.
/// </summary>
public interface IKeyBindingsStore : IKeyMap
{
    /// <summary>Bumps on every change, so a projection of the table can follow it.</summary>
    IReadable<int> Version { get; }

    /// <summary>Whether the command still runs on its built-in gestures.</summary>
    bool IsDefault(KeyCommand command);

    /// <summary>The built-in gestures, whatever the command is bound to now.</summary>
    IReadOnlyList<KeyGesture> DefaultsFor(KeyCommand command);

    /// <summary>Binds the command to this one gesture in place of everything it had.</summary>
    void Rebind(KeyCommand command, KeyGesture gesture);

    /// <summary>Puts the command back on its built-in gestures.</summary>
    void Reset(KeyCommand command);

    /// <summary>Puts every command back on its built-in gestures.</summary>
    void ResetAll();

    /// <summary>The commands not on their defaults, in declaration order — what is worth storing.</summary>
    IReadOnlyList<KeyBinding> Overrides { get; }

    /// <summary>
    /// The other commands a gesture would also fire for, were the command bound to it: those on the
    /// same surface, and the app-wide ones, since either shares a dispatch path with it. Commands on
    /// unrelated surfaces are left out — the same letter on a list and in a terminal never collide.
    /// </summary>
    IReadOnlyList<KeyCommand> ConflictsWith(KeyCommand command, KeyGesture gesture);
}

public static class KeyMapContext
{
    /// <summary>
    /// The keymap in scope. The immutable defaults answer where none was registered, so a surface
    /// built outside the app's wiring still has its keys.
    /// </summary>
    public static IKeyMap KeyMap(this Context ctx) => ctx.Get<IKeyMap>() ?? Input.KeyMap.Defaults;
}
