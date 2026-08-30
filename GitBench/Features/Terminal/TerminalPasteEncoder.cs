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
