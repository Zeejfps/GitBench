namespace GitBench.Features.Assistant.Tools;

/// <summary>One path the query reached, and how well.</summary>
internal readonly record struct PathMatch(string Path, int Score);

/// <summary>
/// Ranks repo-relative paths against what the model asked for.
/// </summary>
/// <remarks>
/// The query is rarely a path. It is a bare file name whose directory the model never saw, a path
/// remembered one segment wrong, a name with two letters swapped, or a glob. So the ladder below
/// walks from "this is the path" down to "these letters appear in this order", and every rung
/// carries a score, because the caller needs an order and a floor — a suggestion worth printing has
/// to be distinguishable from the least-bad row of a thousand.
/// Matching is case-insensitive: a model that mis-cases a path is as common as one that mis-spells
/// it, and no repository is worth showing nothing over that.
/// </remarks>
internal static class PathSearch
{
    /// <summary>Below this a match is a coincidence rather than an answer.</summary>
    public const int SuggestionFloor = 400;

    public static IReadOnlyList<PathMatch> Rank(IEnumerable<string> paths, string? query, int limit)
    {
        var normalized = Normalize(query);
        if (normalized.Length == 0 || limit <= 0) return [];

        var queryName = LastSegment(normalized);
        var isGlob = normalized.Contains('*') || normalized.Contains('?');
        var matches = new List<PathMatch>();

        foreach (var path in paths)
        {
            var lower = path.ToLowerInvariant();
            var score = isGlob
                ? GlobScore(lower, LastSegment(lower), normalized, queryName)
                : Score(lower, LastSegment(lower), normalized, queryName);
            if (score > 0) matches.Add(new PathMatch(path, score));
        }

        matches.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            var byLength = a.Path.Length.CompareTo(b.Path.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Path, b.Path);
        });

        return matches.Count > limit ? matches.GetRange(0, limit) : matches;
    }

    public static string Normalize(string? query) =>
        query is null ? string.Empty : query.Trim().Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();

    private static int Score(string path, string name, string query, string queryName)
    {
        if (path == query) return 1000;
        if (path.EndsWith('/' + query)) return 900;
        if (name == queryName) return query == queryName ? 850 : 800;
        if (Stem(name) == Stem(queryName)) return 750;
        if (path.Contains(query)) return 700;
        if (name.Contains(queryName)) return 650;

        var distance = Math.Min(
            Distance(name, queryName, TypoBudget(queryName)),
            Distance(Stem(name), Stem(queryName), TypoBudget(Stem(queryName))));
        if (distance >= 0) return 600 - (distance * 50);

        if (IsSubsequence(name, queryName)) return 380;
        return IsSubsequence(path, query) ? 300 : 0;
    }

    private static int GlobScore(string path, string name, string query, string queryName)
    {
        if (Glob(path, query)) return 900;
        return Glob(name, queryName) ? 800 : 0;
    }

    private static string LastSegment(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string Stem(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    // Long names absorb more damage before the match stops meaning anything; a three-letter query
    // one edit away from a candidate is already noise.
    private static int TypoBudget(string query) => query.Length switch
    {
        < 4 => 0,
        < 8 => 1,
        _ => 2,
    };

    /// <summary>Levenshtein distance, or -1 once it is certain to exceed <paramref name="budget"/>.</summary>
    private static int Distance(string a, string b, int budget)
    {
        if (budget <= 0) return a == b ? 0 : -1;
        if (Math.Abs(a.Length - b.Length) > budget) return -1;
        if (a.Length == 0 || b.Length == 0) return Math.Max(a.Length, b.Length) <= budget ? Math.Max(a.Length, b.Length) : -1;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                best = Math.Min(best, current[j]);
            }

            if (best > budget) return -1;
            (previous, current) = (current, previous);
        }

        var distance = previous[b.Length];
        return distance <= budget ? distance : -1;
    }

    private static bool IsSubsequence(string text, string query)
    {
        if (query.Length == 0) return false;
        var index = 0;
        foreach (var character in text)
        {
            if (character != query[index]) continue;
            if (++index == query.Length) return true;
        }

        return false;
    }

    // '*' spans anything including separators and '?' takes one character, so a pattern can be
    // written without knowing how deep the file sits.
    private static bool Glob(string text, string pattern)
    {
        var t = 0;
        var p = 0;
        var starText = -1;
        var starPattern = -1;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starText = t;
            }
            else if (starPattern >= 0)
            {
                p = starPattern + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
