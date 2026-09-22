using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;

namespace GitBench.Features.Editor;

/// <summary>
/// What can be offered without a language server: the file's own declarations, every other
/// identifier written in it, and its language's keywords.
/// </summary>
internal static class LocalCompletions
{
    /// <summary>How far from the caret words are gathered in a file too long to read whole on a
    /// keystroke.</summary>
    private const int WordRadiusLines = 5000;

    private const int MinWordLength = 2;

    public static IReadOnlyList<CompletionItem> Collect(
        TextDocument document, TextPosition caret, FileOutline? outline, IReadOnlyCollection<string> keywords)
    {
        var items = new Dictionary<string, CompletionKind>(StringComparer.Ordinal);

        var first = Math.Max(1, caret.Line.Value - WordRadiusLines);
        var last = Math.Min(document.LineCount, caret.Line.Value + WordRadiusLines);
        for (var number = first; number <= last; number++)
        {
            var line = document.Line(new FileLine(number));
            var onCaretLine = number == caret.Line.Value;
            var i = 0;
            while (i < line.Length)
            {
                if (!IsIdentifierStart(line[i]))
                {
                    i++;
                    continue;
                }

                var start = i;
                while (i < line.Length && IsIdentifierPart(line[i])) i++;
                if (i - start < MinWordLength) continue;
                // The word being typed is not a suggestion for itself.
                if (onCaretLine && start <= caret.Column.Value && caret.Column.Value <= i) continue;
                items.TryAdd(line[start..i], CompletionKind.AWord);
            }
        }

        foreach (var keyword in keywords) items[keyword] = CompletionKind.AKeyword;

        if (outline is not null)
            foreach (var node in Walk(outline.Roots))
                if (node.Kind != SymbolKind.Namespace && node.Name.Length > 0 && !IsBeingTyped(node, caret))
                    items[node.Name] = new CompletionKind.Symbol(node.Kind);

        var list = new List<CompletionItem>(items.Count);
        foreach (var (label, kind) in items) list.Add(new CompletionItem(label, kind));
        return list;
    }

    /// <summary>The identifier characters immediately left of <paramref name="column"/>, which is
    /// what a completion replaces.</summary>
    public static int PrefixStart(string line, int column)
    {
        var start = Math.Min(column, line.Length);
        while (start > 0 && IsIdentifierPart(line[start - 1])) start--;
        while (start < column && !IsIdentifierStart(line[start])) start++;
        return start;
    }

    /// <summary>Where the identifier the caret sits in ends, which is what Tab replaces up to.</summary>
    public static int WordEnd(string line, int column)
    {
        var end = Math.Min(column, line.Length);
        while (end < line.Length && IsIdentifierPart(line[end])) end++;
        return end;
    }

    public static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    public static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsBeingTyped(OutlineNode node, TextPosition caret) =>
        node.NameLine == caret.Line
        && node.NameColumn.Value <= caret.Column.Value
        && caret.Column.Value <= node.NameColumn.Value + node.Name.Length;

    private static IEnumerable<OutlineNode> Walk(IReadOnlyList<OutlineNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Walk(node.Children)) yield return child;
        }
    }
}

/// <summary>
/// A language's keywords, read off the literal words its bundled highlight query colors — so every
/// grammar we ship has them and none is a list kept by hand. A language highlighted by TextMate
/// alone has none, and falls back on the words its file already uses.
/// </summary>
internal static partial class CompletionKeywords
{
    private static readonly ConcurrentDictionary<CodeLanguage, IReadOnlyCollection<string>> Cache = new();

    public static IReadOnlyCollection<string> For(string path) =>
        FileLanguage.Detect(path) switch
        {
            FileLanguage.TreeSitter(var language) => Cache.GetOrAdd(language, Read),
            FileLanguage.TextMate or FileLanguage.None => [],
            var other => throw new ArgumentOutOfRangeException(nameof(path), other, null),
        };

    private static IReadOnlyCollection<string> Read(CodeLanguage language) =>
        TreeSitterGrammars.ReadEmbeddedHighlightQuery(language) is { } query ? FromQuery(query) : [];

    /// <summary>The identifier-shaped string literals in a query, skipping the regular expressions
    /// its predicates match with.</summary>
    internal static IReadOnlyCollection<string> FromQuery(string query)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in query.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(';')) continue;
            if (line.Contains("match?", StringComparison.Ordinal)) continue;
            foreach (Match literal in Literal().Matches(line))
                words.Add(literal.Groups[1].Value);
        }

        return words;
    }

    [GeneratedRegex("\"([A-Za-z_][A-Za-z0-9_]+)\"")]
    private static partial Regex Literal();
}
