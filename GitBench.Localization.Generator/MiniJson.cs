using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GitBench.Localization.Generator;

internal sealed class Entry
{
    public string Key = "";
    public string? Text;
    public List<KeyValuePair<string, string>>? Plural;
    public bool IsPlural => Plural != null;
}

internal static class MiniJson
{
    public static List<Entry> Parse(string json)
    {
        var pos = 0;
        var result = new List<Entry>();

        SkipWhitespace(json, ref pos);
        Expect(json, ref pos, '{');
        SkipWhitespace(json, ref pos);

        if (Peek(json, pos) == '}')
        {
            pos++;
            return result;
        }

        while (true)
        {
            SkipWhitespace(json, ref pos);
            var key = ParseString(json, ref pos);
            SkipWhitespace(json, ref pos);
            Expect(json, ref pos, ':');
            SkipWhitespace(json, ref pos);

            var next = Peek(json, pos);
            var entry = new Entry { Key = key };
            if (next == '"')
                entry.Text = ParseString(json, ref pos);
            else if (next == '{')
                entry.Plural = ParseStringObject(json, ref pos);
            else
                throw new FormatException($"Value for key '{key}' must be a string or a plural object.");

            result.Add(entry);

            SkipWhitespace(json, ref pos);
            var after = Peek(json, pos);
            if (after == ',')
            {
                pos++;
                continue;
            }

            if (after == '}')
            {
                pos++;
                break;
            }

            throw new FormatException($"Expected ',' or '}}' after value for key '{key}'.");
        }

        return result;
    }

    private static List<KeyValuePair<string, string>> ParseStringObject(string json, ref int pos)
    {
        Expect(json, ref pos, '{');
        var result = new List<KeyValuePair<string, string>>();
        SkipWhitespace(json, ref pos);
        if (Peek(json, pos) == '}')
        {
            pos++;
            return result;
        }

        while (true)
        {
            SkipWhitespace(json, ref pos);
            var key = ParseString(json, ref pos);
            SkipWhitespace(json, ref pos);
            Expect(json, ref pos, ':');
            SkipWhitespace(json, ref pos);

            if (Peek(json, pos) != '"')
                throw new FormatException($"Plural form '{key}' must be a string.");

            var value = ParseString(json, ref pos);
            result.Add(new KeyValuePair<string, string>(key, value));

            SkipWhitespace(json, ref pos);
            var after = Peek(json, pos);
            if (after == ',')
            {
                pos++;
                continue;
            }

            if (after == '}')
            {
                pos++;
                break;
            }

            throw new FormatException($"Expected ',' or '}}' in plural object for '{key}'.");
        }

        return result;
    }

    private static string ParseString(string json, ref int pos)
    {
        Expect(json, ref pos, '"');
        var sb = new StringBuilder();
        while (true)
        {
            if (pos >= json.Length)
                throw new FormatException("Unterminated string.");

            var c = json[pos++];
            if (c == '"')
                break;

            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            if (pos >= json.Length)
                throw new FormatException("Unterminated escape sequence.");

            var esc = json[pos++];
            switch (esc)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (pos + 4 > json.Length)
                        throw new FormatException("Truncated \\u escape.");
                    var hex = json.Substring(pos, 4);
                    sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    pos += 4;
                    break;
                default:
                    throw new FormatException($"Invalid escape '\\{esc}'.");
            }
        }

        return sb.ToString();
    }

    private static void SkipWhitespace(string json, ref int pos)
    {
        while (pos < json.Length && char.IsWhiteSpace(json[pos]))
            pos++;
    }

    private static char Peek(string json, int pos) => pos < json.Length ? json[pos] : '\0';

    private static void Expect(string json, ref int pos, char expected)
    {
        if (pos >= json.Length || json[pos] != expected)
            throw new FormatException($"Expected '{expected}' at position {pos}.");
        pos++;
    }
}
