using GitBench.Terminal.Vt;

namespace GitBench.Features.Terminal;

/// <summary>
/// A key a terminal has an encoding for, named independently of any keyboard or windowing API.
/// </summary>
/// <remarks>
/// <para>
/// Letters are here because <c>Ctrl</c> and <c>Alt</c> chords are encodings of the key, not of the
/// character it types; an unmodified letter encodes to nothing and reaches the shell through the
/// text pipeline instead, which is what keeps IME, dead keys and non-US layouts working.
/// </para>
/// <para>
/// Public, unlike the encoder that reads it, only so that an xunit <c>[Theory]</c> row can name a
/// member: a test method must be public and a public method cannot take an internal parameter type
/// (CS0051). Same reason as <c>HeadUpstreamState</c> and <c>BranchUpstreamKind</c> in Features/Branches.
/// </para>
/// </remarks>
public enum TerminalKey
{
    /// <summary>A key the terminal has nothing to send for.</summary>
    None = 0,

    Enter,
    Tab,
    Backspace,
    Escape,
    Space,

    Up,
    Down,
    Right,
    Left,
    Home,
    End,

    Insert,
    Delete,
    PageUp,
    PageDown,

    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,

    A,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,
}

/// <summary>The modifiers held down with a key.</summary>
/// <remarks>
/// The values are the xterm modifier bits, so the parameter a modified sequence carries is
/// <c>(int)modifiers + 1</c> rather than a mapping table.
/// </remarks>
[Flags]
public enum TerminalKeyModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Ctrl = 4,
}

/// <summary>How a key press should reach the shell.</summary>
/// <remarks>
/// Three outcomes rather than a byte count, because "wrote nothing" is two different instructions to
/// the pane and getting them confused deletes keystrokes. A letter's character is still on its way
/// from the operating system, so the pane has to claim the key to keep it; nothing at all follows
/// <c>Ctrl+/</c>, so claiming that one sends it neither to the shell nor to the application.
/// </remarks>
internal enum TerminalKeyDelivery
{
    /// <summary>Nothing to send and no character coming: the pane should decline the key.</summary>
    None,

    /// <summary>The operating system will deliver a character; the pane should claim the key as text.</summary>
    Text,

    /// <summary>Bytes were written; the pane should send them and claim the key as a command.</summary>
    Sequence,
}

/// <summary>
/// Turns a key press into the bytes a program running on the terminal expects to read for it.
/// </summary>
/// <remarks>
/// <para>
/// Legacy encoding: the xterm/VT sequences every program understands. The progressive protocols a
/// program can negotiate (<see cref="TerminalModes.KeyboardProtocolFlags"/> and
/// <see cref="TerminalModes.ModifyOtherKeys"/>) are read as "0 means legacy", and 0 is all this
/// encodes — the modes are a parameter so that adding one later is a branch here rather than a new
/// caller. That is also why <see cref="TerminalKeyDelivery.Text"/> is decided here and not by the
/// pane: under kitty's report-all-keys flag a bare letter stops producing a character and starts
/// encoding, which is a change to this table rather than to the rule the pane follows.
/// </para>
/// <para>
/// Pure and static on purpose: a key encoding is a table, and a table is worth testing as one. The
/// translation from a windowing system's key codes is <see cref="TerminalKeyMap"/>'s.
/// </para>
/// </remarks>
internal static class TerminalKeyEncoder
{
    /// <summary>The most bytes <see cref="Encode"/> can write for any key and modifier combination.</summary>
    public const int MaxEncodedBytes = 8;

    const TerminalKeyModifiers Expressible =
        TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Ctrl;

    const byte Escape = 0x1B;

    /// <summary>
    /// Decides how one key press reaches the shell, writing its bytes when it has some.
    /// </summary>
    /// <param name="written">
    /// How many bytes of <paramref name="destination"/> were filled. Zero unless the result is
    /// <see cref="TerminalKeyDelivery.Sequence"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is shorter than the sequence. Half an escape sequence is worse
    /// than none, because the program reads the next keystroke as its tail.
    /// </exception>
    public static TerminalKeyDelivery Encode(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        TerminalModes modes,
        Span<byte> destination,
        out int written)
    {
        written = 0;

        var held = modifiers & Expressible;
        Span<byte> sequence = stackalloc byte[MaxEncodedBytes];
        var length = Write(key, held, modes, sequence);

        if (length == 0)
            return TypesACharacter(key, held) ? TerminalKeyDelivery.Text : TerminalKeyDelivery.None;

        if (destination.Length < length)
            throw new ArgumentException(
                $"{key} with {held} encodes to {length} bytes and was given {destination.Length}.",
                nameof(destination));

        sequence[..length].CopyTo(destination);
        written = length;
        return TerminalKeyDelivery.Sequence;
    }

    static int Write(
        TerminalKey key,
        TerminalKeyModifiers held,
        TerminalModes modes,
        Span<byte> destination) => key switch
    {
        TerminalKey.Enter => Bare(0x0D, held, destination),
        TerminalKey.Escape => Bare(Escape, held, destination),
        TerminalKey.Backspace => Bare(
            Holds(held, TerminalKeyModifiers.Ctrl) ? (byte)0x08 : (byte)0x7F,
            held,
            destination),
        TerminalKey.Tab => WriteTab(held, destination),
        TerminalKey.Space => WriteSpace(held, destination),
        >= TerminalKey.A and <= TerminalKey.Z => WriteLetter(key, held, destination),

        TerminalKey.Up => WriteCursor((byte)'A', held, modes, destination),
        TerminalKey.Down => WriteCursor((byte)'B', held, modes, destination),
        TerminalKey.Right => WriteCursor((byte)'C', held, modes, destination),
        TerminalKey.Left => WriteCursor((byte)'D', held, modes, destination),
        TerminalKey.Home => WriteCursor((byte)'H', held, modes, destination),
        TerminalKey.End => WriteCursor((byte)'F', held, modes, destination),

        TerminalKey.Insert => WriteTilde(2, held, destination),
        TerminalKey.Delete => WriteTilde(3, held, destination),
        TerminalKey.PageUp => WriteTilde(5, held, destination),
        TerminalKey.PageDown => WriteTilde(6, held, destination),

        TerminalKey.F1 => WriteFunction((byte)'P', held, destination),
        TerminalKey.F2 => WriteFunction((byte)'Q', held, destination),
        TerminalKey.F3 => WriteFunction((byte)'R', held, destination),
        TerminalKey.F4 => WriteFunction((byte)'S', held, destination),
        TerminalKey.F5 => WriteTilde(15, held, destination),
        TerminalKey.F6 => WriteTilde(17, held, destination),
        TerminalKey.F7 => WriteTilde(18, held, destination),
        TerminalKey.F8 => WriteTilde(19, held, destination),
        TerminalKey.F9 => WriteTilde(20, held, destination),
        TerminalKey.F10 => WriteTilde(21, held, destination),
        TerminalKey.F11 => WriteTilde(23, held, destination),
        TerminalKey.F12 => WriteTilde(24, held, destination),

        _ => 0,
    };

    static bool TypesACharacter(TerminalKey key, TerminalKeyModifiers held) =>
        (key == TerminalKey.Space || key is >= TerminalKey.A and <= TerminalKey.Z)
        && (held & ~TerminalKeyModifiers.Shift) == 0;

    static bool Holds(TerminalKeyModifiers held, TerminalKeyModifiers modifier) =>
        (held & modifier) != 0;

    static int Parameter(TerminalKeyModifiers held) => (int)held + 1;

    static int Bare(byte value, TerminalKeyModifiers held, Span<byte> destination)
    {
        var length = 0;
        if (Holds(held, TerminalKeyModifiers.Alt)) destination[length++] = Escape;
        destination[length++] = value;
        return length;
    }

    static int WriteTab(TerminalKeyModifiers held, Span<byte> destination) =>
        Holds(held, TerminalKeyModifiers.Shift)
            ? Csi((byte)'Z', destination)
            : Bare(0x09, held, destination);

    static int WriteSpace(TerminalKeyModifiers held, Span<byte> destination)
    {
        if (Holds(held, TerminalKeyModifiers.Ctrl)) return Bare(0x00, held, destination);
        if (Holds(held, TerminalKeyModifiers.Alt)) return Bare((byte)' ', held, destination);
        return 0;
    }

    static int WriteLetter(TerminalKey key, TerminalKeyModifiers held, Span<byte> destination)
    {
        var offset = key - TerminalKey.A;

        if (Holds(held, TerminalKeyModifiers.Ctrl))
            return Bare((byte)(offset + 1), held, destination);

        if (Holds(held, TerminalKeyModifiers.Alt))
        {
            var letter = Holds(held, TerminalKeyModifiers.Shift) ? 'A' : 'a';
            return Bare((byte)(letter + offset), held, destination);
        }

        return 0;
    }

    static int WriteCursor(
        byte final,
        TerminalKeyModifiers held,
        TerminalModes modes,
        Span<byte> destination)
    {
        if (held != TerminalKeyModifiers.None) return Csi(1, Parameter(held), final, destination);

        return modes.ApplicationCursorKeys ? Ss3(final, destination) : Csi(final, destination);
    }

    static int WriteFunction(byte final, TerminalKeyModifiers held, Span<byte> destination) =>
        held == TerminalKeyModifiers.None
            ? Ss3(final, destination)
            : Csi(1, Parameter(held), final, destination);

    static int WriteTilde(int number, TerminalKeyModifiers held, Span<byte> destination) =>
        held == TerminalKeyModifiers.None
            ? Csi(number, (byte)'~', destination)
            : Csi(number, Parameter(held), (byte)'~', destination);

    static int Csi(byte final, Span<byte> destination)
    {
        destination[0] = Escape;
        destination[1] = (byte)'[';
        destination[2] = final;
        return 3;
    }

    static int Ss3(byte final, Span<byte> destination)
    {
        destination[0] = Escape;
        destination[1] = (byte)'O';
        destination[2] = final;
        return 3;
    }

    static int Csi(int number, byte final, Span<byte> destination)
    {
        destination[0] = Escape;
        destination[1] = (byte)'[';
        var length = 2 + Digits(number, destination[2..]);
        destination[length] = final;
        return length + 1;
    }

    static int Csi(int number, int modifier, byte final, Span<byte> destination)
    {
        destination[0] = Escape;
        destination[1] = (byte)'[';
        var length = 2 + Digits(number, destination[2..]);
        destination[length++] = (byte)';';
        length += Digits(modifier, destination[length..]);
        destination[length] = final;
        return length + 1;
    }

    static int Digits(int value, Span<byte> destination)
    {
        if (value < 10)
        {
            destination[0] = (byte)('0' + value);
            return 1;
        }

        destination[0] = (byte)('0' + value / 10);
        destination[1] = (byte)('0' + value % 10);
        return 2;
    }
}
