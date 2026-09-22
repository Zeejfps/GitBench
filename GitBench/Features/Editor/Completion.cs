using GitBench.Features.CodeIntel;

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

internal sealed record CompletionItem(string Label, CompletionKind Kind);

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
            return new CompletionMatch(ExactPrefix - label.Length, Range(0, pattern.Length));
        if (label.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            return new CompletionMatch(Prefix - label.Length, Range(0, pattern.Length));

        if (HumpPositions(pattern, label) is { } humps)
        {
            var gaps = humps[^1] - humps[0] + 1 - humps.Length;
            var score = (humps[0] == 0 ? HumpsFromStart : Humps) - gaps * 4 - label.Length;
            return new CompletionMatch(score, humps);
        }

        var index = label.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        return index >= 0
            ? new CompletionMatch(Substring - index - label.Length, Range(index, pattern.Length))
            : null;
    }

    /// <summary>Ranks a pool against a prefix, best first: score, then where it came from, then the
    /// shorter label, then alphabetical.</summary>
    public static IReadOnlyList<RankedCompletion> Rank(string pattern, IEnumerable<CompletionItem> pool)
    {
        var ranked = new List<RankedCompletion>();
        foreach (var item in pool)
            if (Match(pattern, item.Label) is { } match)
                ranked.Add(new RankedCompletion(item, match));

        ranked.Sort(static (a, b) =>
        {
            var byScore = b.Match.Score.CompareTo(a.Match.Score);
            if (byScore != 0) return byScore;
            var byKind = a.Item.Kind.Precedence.CompareTo(b.Item.Kind.Precedence);
            if (byKind != 0) return byKind;
            var byLength = a.Item.Label.Length.CompareTo(b.Item.Label.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Item.Label, b.Item.Label);
        });
        return ranked;
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
