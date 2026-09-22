namespace GitBench.Features.Editor;

/// <summary>What a language types in pairs, and whether a line ending in a colon opens a block.</summary>
/// <param name="Openers">The brackets that close themselves, each at the same index as its closer.</param>
/// <param name="Closers">The closing brackets, index for index with <paramref name="Openers"/>.</param>
/// <param name="Quotes">The characters that both open and close a string, and so pair with themselves.</param>
internal sealed record TypingRules(string Openers, string Closers, string Quotes, bool ColonOpensBlock)
{
    /// <summary>Plain text: nothing pairs, and Enter only carries the indentation down.</summary>
    public static readonly TypingRules None = new("", "", "", false);

    private const string Brackets = "([{";
    private const string BracketClosers = ")]}";

    /// <summary>The rules for a language, by its TextMate id, or <see cref="None"/> for a file of no
    /// known language.</summary>
    public static TypingRules For(string? languageId) => languageId switch
    {
        null => None,
        "markdown" => new(Brackets, BracketClosers, "`", false),
        "rust" or "clojure" or "json" or "jsonc" => new(Brackets, BracketClosers, "\"", false),
        "javascript" or "javascriptreact" or "typescript" or "typescriptreact" or "go"
            => new(Brackets, BracketClosers, "\"'`", false),
        "python" or "yaml" => new(Brackets, BracketClosers, "\"'", true),
        _ => new(Brackets, BracketClosers, "\"'", false),
    };

    public bool IsOpener(char c) => Openers.Contains(c);

    public bool IsCloser(char c) => Closers.Contains(c);

    public bool IsQuote(char c) => Quotes.Contains(c);

    /// <summary>What closes a bracket or a quote, or null for a character that pairs with nothing.</summary>
    public char? CloserOf(char c)
    {
        var index = Openers.IndexOf(c);
        if (index >= 0) return Closers[index];
        return IsQuote(c) ? c : null;
    }

    public char? OpenerOf(char closer)
    {
        var index = Closers.IndexOf(closer);
        return index >= 0 ? Openers[index] : null;
    }
}

/// <summary>What a position on a line sits inside, as far as that line alone can tell.</summary>
internal abstract record LineContext
{
    private LineContext() { }

    public sealed record Code : LineContext;

    public sealed record InString(char Quote) : LineContext;

    public sealed record InComment : LineContext;

    public static readonly Code InCode = new();

    /// <summary>
    /// Scans one line up to <paramref name="column"/>. Line-local by design: it cannot see a block
    /// comment or a string that began on an earlier line, and answers as if neither had.
    /// </summary>
    public static LineContext At(string line, int column, TypingRules rules, string? lineComment)
    {
        LineContext state = InCode;
        var stop = Math.Min(column, line.Length);
        for (var i = 0; i < stop; i++)
        {
            var c = line[i];
            switch (state)
            {
                case InString(var quote):
                    if (c == '\\') i++;
                    else if (c == quote) state = InCode;
                    break;

                case Code:
                    if (lineComment != null && line.AsSpan(i).StartsWith(lineComment)) return new InComment();
                    if (rules.IsQuote(c)) state = new InString(c);
                    break;

                case InComment:
                    return state;

                default:
                    throw new InvalidOperationException($"Unhandled line context {state}.");
            }
        }

        return state;
    }

    /// <summary>
    /// The line holding the bracket that <paramref name="closer"/>, typed at
    /// <paramref name="column"/> of <paramref name="lineNumber"/>, would close — scanning back no
    /// further than <paramref name="maxLines"/>. Brackets inside strings and comments do not count.
    /// Null when there is none, or when the nearest unclosed bracket is a different kind.
    /// </summary>
    public static int? OpenerLine(
        Func<int, string> lineAt, int lineNumber, int column, char closer, TypingRules rules,
        string? lineComment, int maxLines = 5000)
    {
        var depth = 0;
        var brackets = new List<char>();
        var floor = Math.Max(1, lineNumber - maxLines);
        for (var number = lineNumber; number >= floor; number--)
        {
            var line = lineAt(number);
            CodeBrackets(line, number == lineNumber ? column : line.Length, rules, lineComment, brackets);
            for (var i = brackets.Count - 1; i >= 0; i--)
            {
                var c = brackets[i];
                if (rules.IsCloser(c))
                {
                    depth++;
                    continue;
                }

                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                return rules.CloserOf(c) == closer ? number : null;
            }
        }

        return null;
    }

    private static void CodeBrackets(string line, int stop, TypingRules rules, string? lineComment, List<char> into)
    {
        into.Clear();
        char? quote = null;
        var end = Math.Min(stop, line.Length);
        for (var i = 0; i < end; i++)
        {
            var c = line[i];
            if (quote is { } open)
            {
                if (c == '\\') i++;
                else if (c == open) quote = null;
                continue;
            }

            if (lineComment != null && line.AsSpan(i).StartsWith(lineComment)) return;
            if (rules.IsQuote(c)) quote = c;
            else if (rules.IsOpener(c) || rules.IsCloser(c)) into.Add(c);
        }
    }
}
