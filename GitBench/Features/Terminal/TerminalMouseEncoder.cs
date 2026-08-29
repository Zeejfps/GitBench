using GitBench.Terminal.Vt;

namespace GitBench.Features.Terminal;

public enum TerminalMouseButton
{
    None = 0,

    Left,
    Middle,
    Right,

    WheelUp,
    WheelDown,
}

public enum TerminalMouseAction
{
    Press,
    Release,
    Move,
}

internal static class TerminalMouseEncoder
{
    public const int MaxEncodedBytes = 24;

    const byte Escape = 0x1B;

    const int MotionBit = 32;
    const int LegacyOffset = 32;
    const int LegacyMaxCoordinate = 222;
    const int Utf8MaxCoordinate = 2014;

    public static bool Encode(
        TerminalMouseButton button,
        TerminalMouseAction action,
        int column,
        int row,
        TerminalKeyModifiers modifiers,
        TerminalModes modes,
        Span<byte> destination,
        out int written)
    {
        written = 0;

        if (column < 0 || row < 0) return false;
        if (!IsReported(button, action, modes.MouseTracking)) return false;
        if (!FitsTheEncoding(column, row, modes.MouseEncoding)) return false;

        var code = Code(button, action, modifiers, modes);

        Span<byte> sequence = stackalloc byte[MaxEncodedBytes];
        var length = modes.MouseEncoding switch
        {
            MouseEncoding.Sgr => WriteSgr(code, action, column, row, sequence),
            MouseEncoding.Urxvt => WriteUrxvt(code, column, row, sequence),
            MouseEncoding.Utf8 => WriteUtf8(code, column, row, sequence),
            _ => WriteX10(code, column, row, sequence),
        };

        if (destination.Length < length)
            throw new ArgumentException(
                $"A {action} of {button} encodes to {length} bytes and was given {destination.Length}.",
                nameof(destination));

        sequence[..length].CopyTo(destination);
        written = length;
        return true;
    }

    static bool IsReported(
        TerminalMouseButton button,
        TerminalMouseAction action,
        MouseTracking tracking)
    {
        if (tracking == MouseTracking.Off) return false;

        if (IsWheel(button))
            return action == TerminalMouseAction.Press && tracking >= MouseTracking.Normal;

        return action switch
        {
            TerminalMouseAction.Press => button != TerminalMouseButton.None,
            TerminalMouseAction.Release =>
                button != TerminalMouseButton.None && tracking >= MouseTracking.Normal,
            TerminalMouseAction.Move => tracking == MouseTracking.AnyEvent
                || (tracking == MouseTracking.ButtonEvent && button != TerminalMouseButton.None),
            _ => false,
        };
    }

    static bool FitsTheEncoding(int column, int row, MouseEncoding encoding) => encoding switch
    {
        MouseEncoding.Sgr or MouseEncoding.Urxvt => true,
        MouseEncoding.Utf8 => column <= Utf8MaxCoordinate && row <= Utf8MaxCoordinate,
        _ => column <= LegacyMaxCoordinate && row <= LegacyMaxCoordinate,
    };

    static int Code(
        TerminalMouseButton button,
        TerminalMouseAction action,
        TerminalKeyModifiers modifiers,
        TerminalModes modes)
    {
        var released = action == TerminalMouseAction.Release
            && modes.MouseEncoding != MouseEncoding.Sgr;

        var code = released ? 3 : button switch
        {
            TerminalMouseButton.Left => 0,
            TerminalMouseButton.Middle => 1,
            TerminalMouseButton.Right => 2,
            TerminalMouseButton.WheelUp => 64,
            TerminalMouseButton.WheelDown => 65,
            _ => 3,
        };

        if (action == TerminalMouseAction.Move) code += MotionBit;

        if (modes.MouseTracking != MouseTracking.X10)
        {
            if (modifiers.HasFlag(TerminalKeyModifiers.Shift)) code |= 4;
            if (modifiers.HasFlag(TerminalKeyModifiers.Alt)) code |= 8;
            if (modifiers.HasFlag(TerminalKeyModifiers.Ctrl)) code |= 16;
        }

        return code;
    }

    static int WriteSgr(
        int code,
        TerminalMouseAction action,
        int column,
        int row,
        Span<byte> destination)
    {
        var length = Csi(destination);
        destination[length++] = (byte)'<';
        length += Digits(code, destination[length..]);
        destination[length++] = (byte)';';
        length += Digits(column + 1, destination[length..]);
        destination[length++] = (byte)';';
        length += Digits(row + 1, destination[length..]);
        destination[length++] = action == TerminalMouseAction.Release ? (byte)'m' : (byte)'M';
        return length;
    }

    static int WriteUrxvt(int code, int column, int row, Span<byte> destination)
    {
        var length = Csi(destination);
        length += Digits(code + LegacyOffset, destination[length..]);
        destination[length++] = (byte)';';
        length += Digits(column + 1, destination[length..]);
        destination[length++] = (byte)';';
        length += Digits(row + 1, destination[length..]);
        destination[length++] = (byte)'M';
        return length;
    }

    static int WriteX10(int code, int column, int row, Span<byte> destination)
    {
        var length = Csi(destination);
        destination[length++] = (byte)'M';
        destination[length++] = (byte)(code + LegacyOffset);
        destination[length++] = (byte)(column + 1 + LegacyOffset);
        destination[length++] = (byte)(row + 1 + LegacyOffset);
        return length;
    }

    static int WriteUtf8(int code, int column, int row, Span<byte> destination)
    {
        var length = Csi(destination);
        destination[length++] = (byte)'M';
        length += Utf8(code + LegacyOffset, destination[length..]);
        length += Utf8(column + 1 + LegacyOffset, destination[length..]);
        length += Utf8(row + 1 + LegacyOffset, destination[length..]);
        return length;
    }

    static bool IsWheel(TerminalMouseButton button) =>
        button is TerminalMouseButton.WheelUp or TerminalMouseButton.WheelDown;

    static int Csi(Span<byte> destination)
    {
        destination[0] = Escape;
        destination[1] = (byte)'[';
        return 2;
    }

    static int Utf8(int value, Span<byte> destination)
    {
        if (value < 128)
        {
            destination[0] = (byte)value;
            return 1;
        }

        destination[0] = (byte)(0xC0 | (value >> 6));
        destination[1] = (byte)(0x80 | (value & 0x3F));
        return 2;
    }

    static int Digits(int value, Span<byte> destination)
    {
        var length = 0;
        if (value >= 1000) destination[length++] = (byte)('0' + value / 1000 % 10);
        if (value >= 100) destination[length++] = (byte)('0' + value / 100 % 10);
        if (value >= 10) destination[length++] = (byte)('0' + value / 10 % 10);
        destination[length++] = (byte)('0' + value % 10);
        return length;
    }
}
