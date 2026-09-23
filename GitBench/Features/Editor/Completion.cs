using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Lsp;

namespace GitBench.Features.Editor;

/// <summary>Where a completion came from, which decides its icon and how it ranks against an equal
/// match.</summary>
internal abstract record CompletionKind
{
    private CompletionKind() { }

    /// <summary>A declaration in the open file, named by its outline.</summary>
    public sealed record Symbol(SymbolKind Kind) : CompletionKind;

    /// <summary>A word the language reserves, whether or not the file uses it yet.</summary>
    public sealed record Keyword : CompletionKind;

    /// <summary>Any other identifier written somewhere in the file.</summary>
    public sealed record Word : CompletionKind;

    public static readonly Keyword AKeyword = new();

    public static readonly Word AWord = new();

    /// <summary>Lower ranks first among matches that score the same.</summary>
    public int Precedence => this switch
    {
        Symbol => 0,
        Word => 1,
        Keyword => 2,
        _ => throw new InvalidOperationException($"Unhandled completion kind {this}."),
    };
}

/// <summary>What accepting a completion puts in the file.</summary>
internal abstract record CompletionInsert
{
    private CompletionInsert() { }

    /// <summary>The label itself, over the identifier being typed.</summary>
    public sealed record TheLabel : CompletionInsert;

    public static readonly TheLabel Label = new();

    /// <summary>
    /// What a server said to insert, and where. The ranges are where the server placed it when it
    /// was asked; typing since only ever extends them to the caret.
    /// </summary>
    /// <param name="InsertStart">Where the text begins when it is inserted, or null to begin at the
    /// identifier.</param>
    /// <param name="ReplaceEnd">How far it reaches when it replaces the word, where the server said.</param>
    /// <param name="Additional">Edits elsewhere that come with it — the import a name needs.</param>
    public sealed record ServerEdit(
        string Text, TextPosition? InsertStart, TextPosition? ReplaceEnd, IReadOnlyList<TextEdit> Additional)
        : CompletionInsert;
}

/// <param name="Label">What the list shows, and what is matched when there is no filter text.</param>
internal sealed record CompletionItem(string Label, CompletionKind Kind)
{
    /// <summary>The type or signature shown beside the label, where the server said.</summary>
    public string? Detail { get; init; }

    /// <summary>The server's own order, which breaks ties between equally good matches.</summary>
    public string? SortText { get; init; }

    /// <summary>What a prefix is matched against, where it is not the label.</summary>
    public string? FilterText { get; init; }

    /// <summary>Its documentation as markdown, where the list already carried it.</summary>
    public string? Documentation { get; init; }

    /// <summary>How to ask its server for the documentation the list left out, or null for an item
    /// no server offered.</summary>
    public CompletionItemHandle? Resolve { get; init; }

    public CompletionInsert Insert { get; init; } = CompletionInsert.Label;
}

/// <summary>How well a typed prefix matches a label, and which of the label's characters it
/// matched, for the list to draw them emphasized.</summary>
internal sealed record CompletionMatch(int Score, IReadOnlyList<int> Positions);

internal sealed record RankedCompletion(CompletionItem Item, CompletionMatch Match);

/// <summary>
/// Matches what has been typed against a label the way Rider does: a prefix first, case-sensitive
/// above insensitive, then CamelHumps — <c>gBN</c> finds <c>getBranchName</c>, each typed
/// character either continuing the hump it is in or starting the next — then anywhere in the label.
/// </summary>
internal static class CompletionMatcher
{
    private const int ExactPrefix = 4000;
    private const int Prefix = 3000;
    private const int HumpsFromStart = 2000;
    private const int Humps = 1000;
    private const int Substring = 500;

    public static CompletionMatch? Match(string pattern, string label)
    {
        if (pattern.Length == 0) return new CompletionMatch(0, []);
        if (pattern.Length > label.Length) return null;

        if (label.StartsWith(pattern, StringComparison.Ordinal))
            return new CompletionMatch(ExactPrefix, Range(0, pattern.Length));
        if (label.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            return new CompletionMatch(Prefix, Range(0, pattern.Length));

        if (HumpPositions(pattern, label) is { } humps)
        {
            var gaps = humps[^1] - humps[0] + 1 - humps.Length;
            var score = (humps[0] == 0 ? HumpsFromStart : Humps) - gaps * 4;
            return new CompletionMatch(score, humps);
        }

        var index = label.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        return index >= 0
            ? new CompletionMatch(Substring - index, Range(index, pattern.Length))
            : null;
    }

    /// <summary>Ranks a pool against a prefix, best first: how well it matched, then where it came
    /// from, then the server's own order, then the shorter label, then alphabetical.</summary>
    public static IReadOnlyList<RankedCompletion> Rank(string pattern, IEnumerable<CompletionItem> pool)
    {
        var ranked = new List<RankedCompletion>();
        foreach (var item in pool)
            if (MatchItem(pattern, item) is { } match)
                ranked.Add(new RankedCompletion(item, match));

        ranked.Sort(static (a, b) =>
        {
            var byScore = b.Match.Score.CompareTo(a.Match.Score);
            if (byScore != 0) return byScore;
            var byKind = a.Item.Kind.Precedence.CompareTo(b.Item.Kind.Precedence);
            if (byKind != 0) return byKind;
            if (a.Item.SortText is { } left && b.Item.SortText is { } right)
            {
                var bySort = string.CompareOrdinal(left, right);
                if (bySort != 0) return bySort;
            }
            var byLength = a.Item.Label.Length.CompareTo(b.Item.Label.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Item.Label, b.Item.Label);
        });
        return ranked;
    }

    /// <summary>Scored against the filter text where there is one; the characters drawn emphasized
    /// are the label's, and none when the label itself does not match.</summary>
    private static CompletionMatch? MatchItem(string pattern, CompletionItem item)
    {
        if (item.FilterText is not { } filter || filter == item.Label) return Match(pattern, item.Label);
        if (Match(pattern, filter) is not { } scored) return null;
        return Match(pattern, item.Label) is { } shown ? scored with { Positions = shown.Positions } : scored with { Positions = [] };
    }

    private static int[]? HumpPositions(string pattern, string label)
    {
        var positions = new int[pattern.Length];
        var at = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var wanted = pattern[i];
            if (i > 0 && at < label.Length && SameLetter(label[at], wanted))
            {
                positions[i] = at++;
                continue;
            }

            var found = -1;
            for (var k = at; k < label.Length; k++)
            {
                if (IsHumpStart(label, k) && SameLetter(label[k], wanted))
                {
                    found = k;
                    break;
                }
            }

            if (found < 0) return null;
            positions[i] = found;
            at = found + 1;
        }

        return positions;
    }

    private static bool IsHumpStart(string label, int index)
    {
        if (index == 0) return true;
        var c = label[index];
        var before = label[index - 1];
        if (before is '_' or '-' or '$') return c is not ('_' or '-' or '$');
        if (char.IsUpper(c)) return !char.IsUpper(before) || (index + 1 < label.Length && char.IsLower(label[index + 1]));
        return char.IsDigit(c) && !char.IsDigit(before);
    }

    private static bool SameLetter(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    private static int[] Range(int start, int count)
    {
        var positions = new int[count];
        for (var i = 0; i < count; i++) positions[i] = start + i;
        return positions;
    }
}
