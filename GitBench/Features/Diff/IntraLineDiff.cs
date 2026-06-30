using System.Runtime.CompilerServices;
using GitBench.Git;

namespace GitBench.Features.Diff;

/// <summary>
/// Computes intra-line (changed-character) emphasis for a hunk: when a replace block swaps
/// similar line(s), highlight only the words that actually changed on each side rather than
/// tinting the whole line. Pure and view-local — operates on tab-expanded text, owns no color,
/// and never mutates the underlying <see cref="DiffLine"/> records.
/// </summary>
internal static class IntraLineDiff
{
    // A paired (old, new) line must keep at least this fraction of the longer line's characters
    // in common (prefix + suffix + matched middle tokens) to earn emphasis. Below it the pair is
    // a wholesale rewrite — or a mispairing from index-wise pairing within an unbalanced block —
    // and emits nothing, so the line just reads as a plain delete+add instead of full-line noise.
    private const double MinSimilarity = 0.30;

    // Cost guard: if either side's differing middle exceeds this many chars, skip the LCS and
    // emit nothing (treat as a full-line change) to bound the O(n·m) token diff.
    private const int MaxMiddleLength = 2000;

    // Keyed on the hunk's Lines reference, which is reference-stable across the double flatten
    // (FlattenRows runs once per emit). GetValue inserts atomically — no TryGetValue/Add race —
    // and the weak key means entries are collected with the hunk, so no eviction or leak.
    private static readonly ConditionalWeakTable<IReadOnlyList<DiffLine>, IReadOnlyList<CharRange>?[]>
        Cache = new();

    /// <summary>
    /// Per-line emphasis for one hunk, indexed by position in <paramref name="lines"/>.
    /// <paramref name="expandedTexts"/>[i] must be <c>DiffText.ExpandTabs(lines[i].Text)</c>.
    /// Entry is null where there is no emphasis. Memoized on the <paramref name="lines"/>
    /// reference; a cache hit ignores <paramref name="expandedTexts"/> (a deterministic function
    /// of <paramref name="lines"/>, so already baked into the cached result).
    /// </summary>
    public static IReadOnlyList<CharRange>?[] ForHunk(
        IReadOnlyList<DiffLine> lines, IReadOnlyList<string> expandedTexts) =>
        Cache.GetValue(lines, _ => Compute(lines, expandedTexts));

    private static IReadOnlyList<CharRange>?[] Compute(
        IReadOnlyList<DiffLine> lines, IReadOnlyList<string> expandedTexts)
    {
        var result = new IReadOnlyList<CharRange>?[lines.Count];
        var i = 0;
        while (i < lines.Count)
        {
            // A replace block is a maximal run of Removed immediately followed by a maximal run
            // of Added (no intervening Context). Anything else advances without pairing.
            if (lines[i].Kind != DiffLineKind.Removed) { i++; continue; }
            var removedStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed) i++;
            var addedStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Added) i++;

            var pairs = Math.Min(addedStart - removedStart, i - addedStart);
            for (var k = 0; k < pairs; k++)
            {
                var oldIdx = removedStart + k;
                var newIdx = addedStart + k;
                var (oldRanges, newRanges) = ForPair(expandedTexts[oldIdx], expandedTexts[newIdx]);
                if (oldRanges.Count > 0) result[oldIdx] = oldRanges;
                if (newRanges.Count > 0) result[newIdx] = newRanges;
            }
        }
        return result;
    }

    /// <summary>
    /// Changed-character ranges for a single paired line. Both lists are sorted, non-overlapping,
    /// in-bounds, and non-zero-length (the renderer walks them in one incremental pass).
    /// Returns empty lists for identical lines, full rewrites (below the similarity gate), and
    /// middles past the cost guard.
    /// </summary>
    public static (IReadOnlyList<CharRange> Old, IReadOnlyList<CharRange> New) ForPair(
        string oldExpanded, string newExpanded)
    {
        if (oldExpanded == newExpanded) return Empty;

        var prefix = CommonPrefix(oldExpanded, newExpanded);
        var suffix = CommonSuffix(oldExpanded, newExpanded, prefix);

        var oldMidLen = oldExpanded.Length - prefix - suffix;
        var newMidLen = newExpanded.Length - prefix - suffix;
        if (oldMidLen == 0 && newMidLen == 0) return Empty;
        if (oldMidLen > MaxMiddleLength || newMidLen > MaxMiddleLength) return Empty;

        var oldTokens = Tokenize(oldExpanded, prefix, oldMidLen);
        var newTokens = Tokenize(newExpanded, prefix, newMidLen);

        var (oldMatched, newMatched, matchedChars) = LcsMatch(oldTokens, newTokens);

        var longer = Math.Max(oldExpanded.Length, newExpanded.Length);
        if (prefix + suffix + matchedChars < MinSimilarity * longer) return Empty;

        return (BuildRanges(oldTokens, oldMatched), BuildRanges(newTokens, newMatched));
    }

    private static readonly (IReadOnlyList<CharRange>, IReadOnlyList<CharRange>) Empty =
        (Array.Empty<CharRange>(), Array.Empty<CharRange>());

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static int CommonSuffix(string a, string b, int prefix)
    {
        // Cap so the suffix can't overlap the already-counted prefix on the shorter side.
        var max = Math.Min(a.Length, b.Length) - prefix;
        var i = 0;
        while (i < max && a[a.Length - 1 - i] == b[b.Length - 1 - i]) i++;
        return i;
    }

    private readonly record struct Token(int Start, int Length, string Text);

    // Word ([A-Za-z0-9_]+), whitespace run, or single symbol — word granularity reads better
    // than per-char. Starts are absolute offsets into the full line (origin + position in middle).
    private static List<Token> Tokenize(string s, int origin, int midLen)
    {
        var tokens = new List<Token>();
        var end = origin + midLen;
        var i = origin;
        while (i < end)
        {
            var start = i;
            var c = s[i];
            if (IsWordChar(c))
                while (i < end && IsWordChar(s[i])) i++;
            else if (char.IsWhiteSpace(c))
                while (i < end && char.IsWhiteSpace(s[i])) i++;
            else
                i++;
            tokens.Add(new Token(start, i - start, s.Substring(start, i - start)));
        }
        return tokens;
    }

    private static bool IsWordChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';

    private static (bool[] OldMatched, bool[] NewMatched, int MatchedChars) LcsMatch(
        List<Token> a, List<Token> b)
    {
        var n = a.Count;
        var m = b.Count;
        var oldMatched = new bool[n];
        var newMatched = new bool[m];
        if (n == 0 || m == 0) return (oldMatched, newMatched, 0);

        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = a[i].Text == b[j].Text
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var matchedChars = 0;
        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (a[x].Text == b[y].Text)
            {
                oldMatched[x] = true;
                newMatched[y] = true;
                matchedChars += a[x].Length;
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1]) x++;
            else y++;
        }
        return (oldMatched, newMatched, matchedChars);
    }

    // Coalesce runs of consecutive unmatched tokens into one range. Tokens partition the middle
    // contiguously, so a run of unmatched tokens is itself contiguous → one merged range.
    private static List<CharRange> BuildRanges(List<Token> tokens, bool[] matched)
    {
        var ranges = new List<CharRange>();
        var i = 0;
        while (i < tokens.Count)
        {
            if (matched[i]) { i++; continue; }
            var start = tokens[i].Start;
            var end = tokens[i].Start + tokens[i].Length;
            i++;
            while (i < tokens.Count && !matched[i])
            {
                end = tokens[i].Start + tokens[i].Length;
                i++;
            }
            ranges.Add(new CharRange(start, end - start));
        }
        return ranges;
    }
}
