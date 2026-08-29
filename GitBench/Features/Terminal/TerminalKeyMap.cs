using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Terminal;

/// <summary>
/// Names the keys of a desktop keyboard as the keys a terminal has encodings for.
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than a switch inside the controller, because it is a table of the same kind
/// as <see cref="TerminalKeyEncoder"/>'s and fails the same way — a missing arm is a key that
/// silently does nothing, and a switch reachable only by pressing every key through a harness is one
/// no test enumerates. Splitting it out lets a test walk both enums instead.
/// </para>
/// <para>
/// This is also where the layout problem lives. <see cref="KeyboardKey"/> is a physical position, so
/// the keys whose control chords depend on what character the position produces — <c>Ctrl+[</c>,
/// <c>Ctrl+]</c>, <c>Ctrl+\</c> — have no honest mapping here yet, and AltGr arrives indistinguishable
/// from Ctrl+Alt. Both are decisions about this table, not about the encoding.
/// </para>
/// </remarks>
internal static class TerminalKeyMap
{
    /// <summary>
    /// The terminal key this position stands for, or <see cref="TerminalKey.None"/> when the
    /// terminal has no encoding for it.
    /// </summary>
    public static TerminalKey From(KeyboardKey key) => key switch
    {
        KeyboardKey.Enter or KeyboardKey.NumpadEnter => TerminalKey.Enter,
        KeyboardKey.Tab => TerminalKey.Tab,
        KeyboardKey.Backspace => TerminalKey.Backspace,
        KeyboardKey.Escape => TerminalKey.Escape,
        KeyboardKey.Space => TerminalKey.Space,

        KeyboardKey.UpArrow => TerminalKey.Up,
        KeyboardKey.DownArrow => TerminalKey.Down,
        KeyboardKey.RightArrow => TerminalKey.Right,
        KeyboardKey.LeftArrow => TerminalKey.Left,
        KeyboardKey.Home => TerminalKey.Home,
        KeyboardKey.End => TerminalKey.End,

        KeyboardKey.Insert => TerminalKey.Insert,
        KeyboardKey.Delete => TerminalKey.Delete,
        KeyboardKey.PageUp => TerminalKey.PageUp,
        KeyboardKey.PageDown => TerminalKey.PageDown,

        KeyboardKey.F1 => TerminalKey.F1,
        KeyboardKey.F2 => TerminalKey.F2,
        KeyboardKey.F3 => TerminalKey.F3,
        KeyboardKey.F4 => TerminalKey.F4,
        KeyboardKey.F5 => TerminalKey.F5,
        KeyboardKey.F6 => TerminalKey.F6,
        KeyboardKey.F7 => TerminalKey.F7,
        KeyboardKey.F8 => TerminalKey.F8,
        KeyboardKey.F9 => TerminalKey.F9,
        KeyboardKey.F10 => TerminalKey.F10,
        KeyboardKey.F11 => TerminalKey.F11,
        KeyboardKey.F12 => TerminalKey.F12,

        KeyboardKey.A => TerminalKey.A,
        KeyboardKey.B => TerminalKey.B,
        KeyboardKey.C => TerminalKey.C,
        KeyboardKey.D => TerminalKey.D,
        KeyboardKey.E => TerminalKey.E,
        KeyboardKey.F => TerminalKey.F,
        KeyboardKey.G => TerminalKey.G,
        KeyboardKey.H => TerminalKey.H,
        KeyboardKey.I => TerminalKey.I,
        KeyboardKey.J => TerminalKey.J,
        KeyboardKey.K => TerminalKey.K,
        KeyboardKey.L => TerminalKey.L,
        KeyboardKey.M => TerminalKey.M,
        KeyboardKey.N => TerminalKey.N,
        KeyboardKey.O => TerminalKey.O,
        KeyboardKey.P => TerminalKey.P,
        KeyboardKey.Q => TerminalKey.Q,
        KeyboardKey.R => TerminalKey.R,
        KeyboardKey.S => TerminalKey.S,
        KeyboardKey.T => TerminalKey.T,
        KeyboardKey.U => TerminalKey.U,
        KeyboardKey.V => TerminalKey.V,
        KeyboardKey.W => TerminalKey.W,
        KeyboardKey.X => TerminalKey.X,
        KeyboardKey.Y => TerminalKey.Y,
        KeyboardKey.Z => TerminalKey.Z,

        _ => TerminalKey.None,
    };

    /// <summary>
    /// The modifiers a terminal encodes, dropping the ones it has no form for.
    /// </summary>
    /// <remarks>
    /// <see cref="InputModifiers"/> carries lock state as well as chording modifiers, and Caps and
    /// Num are held down on most real keyboards most of the time. Letting either through would put
    /// them in the xterm modifier parameter and turn every arrow key into a chord no program reads.
    /// </remarks>
    public static TerminalKeyModifiers From(InputModifiers modifiers)
    {
        var held = TerminalKeyModifiers.None;

        if (modifiers.HasFlag(InputModifiers.Shift)) held |= TerminalKeyModifiers.Shift;
        if (modifiers.HasFlag(InputModifiers.Alt)) held |= TerminalKeyModifiers.Alt;
        if (modifiers.HasFlag(InputModifiers.Control)) held |= TerminalKeyModifiers.Ctrl;

        return held;
    }

    /// <summary>
    /// Whether pressing this position can make the operating system deliver a character.
    /// </summary>
    /// <remarks>
    /// The question the pane actually asks, and not the same as "is it a modifier". A key with no
    /// <see cref="TerminalKey"/> is claimed as text so its character can still arrive; claiming one
    /// for a position that never produces a character deletes the keystroke instead, and Menu,
    /// PrintScreen, Pause, the lock keys and F13 upwards are all as silent as Shift is.
    /// </remarks>
    public static bool CanTypeACharacter(KeyboardKey key) => key is
        (>= KeyboardKey.Alpha0 and <= KeyboardKey.Alpha9)
        or (>= KeyboardKey.A and <= KeyboardKey.Z)
        or (>= KeyboardKey.Numpad0 and <= KeyboardKey.Numpad9)
        or KeyboardKey.Space
        or KeyboardKey.Apostrophe
        or KeyboardKey.Comma
        or KeyboardKey.Period
        or KeyboardKey.Slash
        or KeyboardKey.SemiColon
        or KeyboardKey.Equals
        or KeyboardKey.Minus
        or KeyboardKey.LeftBracket
        or KeyboardKey.RightBracket
        or KeyboardKey.Backslash
        or KeyboardKey.GraveAccent
        or KeyboardKey.NumpadDecimal
        or KeyboardKey.NumpadDivide
        or KeyboardKey.NumpadMultiply
        or KeyboardKey.NumpadSubtract
        or KeyboardKey.NumpadAdd
        or KeyboardKey.NumpadEquals;
}
