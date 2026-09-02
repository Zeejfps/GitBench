using System.Text;

namespace GitBench.Features.Terminal;

internal static class TerminalPasteEncoder
{
    public const int MaxPastedCharacters = 1 << 20;

    const char Escape = '\u001b';
    const string Open = "\u001b[200~";
    const string Close = "\u001b[201~";

    public static byte[] Encode(string text, bool bracketed)
    {
        var body = Normalize(text, bracketed);
        if (body.Length == 0) return [];

        return Encoding.UTF8.GetBytes(bracketed ? Open + body + Close : body);
    }

    /// <summary>
    /// How many lines a paste would run as commands, counting from one. Anything above one is worth
    /// asking about before sending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bracketed paste answers zero, because there is nothing to ask: the program has said it will
    /// take the text as text, so the line endings inside it are characters rather than presses of
    /// Enter. Without it every line ending is a press, which is how a paste of a scrollback becomes
    /// a shell running its own prompts back at itself.
    /// </para>
    /// <para>
    /// One trailing line ending does not count. A clipboard holding "git status\n" is one command
    /// the sender meant to run, and a terminal that stopped to ask about that would be a terminal
    /// nobody pastes into twice.
    /// </para>
    /// </remarks>
    public static int LinesToRun(string text, bool bracketed)
    {
        if (bracketed || string.IsNullOrEmpty(text)) return 0;

        var end = text.Length;
        if (end > 0 && text[end - 1] == '\n') end--;
        if (end > 0 && text[end - 1] == '\r') end--;

        var lines = 1;
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '\n') lines++;
            else if (text[i] == '\r') lines += i + 1 < end && text[i + 1] == '\n' ? 0 : 1;
        }

        return lines;
    }

    /// <summary>
    /// The same text as a single line: every run of line endings becomes one space, and the ends are
    /// trimmed. Nothing in it presses Enter, so it lands on the prompt for the sender to read before
    /// they run it.
    /// </summary>
    public static string Flatten(string text)
    {
        var body = new StringBuilder(text.Length);
        var pendingBreak = false;

        foreach (var character in text)
        {
            if (character is '\r' or '\n')
            {
                pendingBreak = body.Length > 0;
                continue;
            }

            if (pendingBreak)
            {
                body.Append(' ');
                pendingBreak = false;
            }

            body.Append(character);
        }

        return body.ToString();
    }

    /// <summary>The first line, for a confirmation to show what is about to run.</summary>
    public static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    static string Normalize(string text, bool bracketed)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var length = Math.Min(text.Length, MaxPastedCharacters);
        var body = new StringBuilder(length);

        for (var i = 0; i < length; i++)
        {
            var character = text[i];

            if (character == '\0') continue;

            if (character == '\r')
            {
                body.Append('\r');
                if (i + 1 < length && text[i + 1] == '\n') i++;
                continue;
            }

            if (character == '\n')
            {
                body.Append('\r');
                continue;
            }

            if (bracketed && character == Escape && StartsTerminator(text, i, length))
            {
                i += Close.Length - 1;
                continue;
            }

            body.Append(character);
        }

        return body.ToString();
    }

    static bool StartsTerminator(string text, int index, int length)
    {
        if (index + Close.Length > length) return false;

        for (var i = 0; i < Close.Length; i++)
            if (text[index + i] != Close[i])
                return false;

        return true;
    }
}
