using System.Text;

namespace GitBench.Pty.Tests;

/// <summary>
/// Reads a pseudo-terminal stream the way a person reads a screen: escape sequences dropped and
/// whitespace squeezed out, so text the terminal wrapped or repainted still matches what the child
/// printed. Assertions on a live terminal stream are containment assertions, never equality.
/// </summary>
static class VtText
{
    public static string Decode(ReadOnlySpan<byte> stream)
    {
        var text = new List<byte>(stream.Length);

        var i = 0;
        while (i < stream.Length)
        {
            var b = stream[i];
            if (b != Escape)
            {
                if (b >= 0x20 || b is (byte)'\n' or (byte)'\r' or (byte)'\t')
                    text.Add(b);
                i++;
                continue;
            }

            i++;
            if (i >= stream.Length)
                break;

            switch ((char)stream[i])
            {
                case '[':
                    i = SkipControlSequence(stream, i + 1);
                    break;
                case ']' or 'P' or '_' or '^' or 'X':
                    i = SkipStringSequence(stream, i + 1);
                    break;
                case '(' or ')' or '*' or '+' or '-' or '.' or '/':
                    i = Math.Min(i + 2, stream.Length);
                    break;
                default:
                    i++;
                    break;
            }
        }

        return Encoding.UTF8.GetString(text.ToArray());
    }

    public static string Squash(string text)
    {
        var squashed = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
                squashed.Append(c);
        }

        return squashed.ToString();
    }

    public static bool Contains(string decoded, string expected) =>
        Squash(decoded).Contains(Squash(expected), StringComparison.OrdinalIgnoreCase);

    const byte Escape = 0x1B;

    static int SkipControlSequence(ReadOnlySpan<byte> stream, int i)
    {
        while (i < stream.Length && stream[i] is >= 0x20 and <= 0x3F)
            i++;

        return i < stream.Length ? i + 1 : i;
    }

    static int SkipStringSequence(ReadOnlySpan<byte> stream, int i)
    {
        while (i < stream.Length)
        {
            if (stream[i] == 0x07)
                return i + 1;

            if (stream[i] == Escape && i + 1 < stream.Length && stream[i + 1] == (byte)'\\')
                return i + 2;

            i++;
        }

        return i;
    }
}
