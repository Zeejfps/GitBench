namespace GitBench.Features.Search;

/// <summary>How well a query matched a name, and which of the name's characters it matched.</summary>
internal readonly record struct NameMatch(int Score, IReadOnlyList<int> Highlights);

/// <summary>A symbol the query reached: the row, its score, and the matched characters of its name.</summary>
internal sealed record RankedSymbol(SymbolRow Row, int Score, IReadOnlyList<int> Highlights);

/// <summary>
/// A query as typed into search: a name, optionally behind the container it sits in. A dot splits
/// the two, so <c>Auth.Login</c> asks for a <c>Login</c> inside something matching <c>Auth</c>.
/// </summary>
internal sealed record SymbolQuery(string? Container, string Name)
{
    public static SymbolQuery Parse(string text)
    {
        var trimmed = text.Trim();
        var dot = trimmed.LastIndexOf('.');
        if (dot <= 0) return new SymbolQuery(null, trimmed.TrimStart('.'));
        return new SymbolQuery(trimmed[..dot], trimmed[(dot + 1)..]);
    }

    public bool IsEmpty => Name.Length == 0 && string.IsNullOrEmpty(Container);
}

/// <summary>
/// Ranks declarations by name: exact, then prefix, then CamelHumps (<c>FBVM</c>, <c>FilBrVM</c>
/// for <c>FileBrowserViewModel</c>), then substring, then the letters in order. Case is ignored
/// for the match and rewarded when it agrees. Types rank above members on a tie.
/// </summary>
internal static class SymbolSearch
{
    private const int Exact = 1000;
    private const int Prefix = 800;
    private const int HumpsFromStart = 700;
    private const int HumpsInside = 650;
    private const int Substring = 600;
    private const int Subsequence = 300;
    private const int CaseBonus = 40;

    /// <summary>Everything after a bare container: <c>Auth.</c> lists what is inside <c>Auth</c>.</summary>
    private const int AnyName = 500;

    public static IReadOnlyList<RankedSymbol> Rank(
        IEnumerable<SymbolRow> rows, SymbolQuery query, Func<SymbolRow, bool> include, int limit)
    {
        if (query.IsEmpty || limit <= 0) return [];

        var ranked = new List<RankedSymbol>();
        foreach (var row in rows)
        {
            if (!include(row)) continue;
            if (Score(query, row) is { } match)
                ranked.Add(new RankedSymbol(row, match.Score, match.Highlights));
        }

        ranked.Sort(Compare);
        return ranked.Count > limit ? ranked.GetRange(0, limit) : ranked;
    }

    public static NameMatch? Score(SymbolQuery query, SymbolRow row)
    {
        NameMatch name;
        if (query.Name.Length == 0)
        {
            name = new NameMatch(AnyName, []);
        }
        else if (Match(query.Name, row.Name) is { } matched)
        {
            name = matched;
        }
        else
        {
            return null;
        }

        if (string.IsNullOrEmpty(query.Container)) return name;
        if (row.Container is null || Match(query.Container, row.Container) is not { } container) return null;
        return name with { Score = name.Score + container.Score / 10 };
    }

    /// <summary>The ordering results are listed in: score, then types before members, then the
    /// shorter name, then stable by name and place.</summary>
    public static int Compare(RankedSymbol a, RankedSymbol b) => Compare(a.Row, a.Score, b.Row, b.Score);

    public static int Compare(SymbolRow a, int aScore, SymbolRow b, int bScore)
    {
        var byScore = bScore.CompareTo(aScore);
        if (byScore != 0) return byScore;
        var byKind = b.IsType.CompareTo(a.IsType);
        if (byKind != 0) return byKind;
        var byLength = a.Name.Length.CompareTo(b.Name.Length);
        if (byLength != 0) return byLength;
        var byName = string.CompareOrdinal(a.Name, b.Name);
        if (byName != 0) return byName;
        var byPath = string.CompareOrdinal(a.Path, b.Path);
        return byPath != 0 ? byPath : a.Line.Value.CompareTo(b.Line.Value);
    }

    /// <summary>How the query matches a name, walking the ladder from exact down to letters in order.</summary>
    public static NameMatch? Match(string query, string name)
    {
        if (query.Length == 0 || query.Length > name.Length) return null;
        // Every rung below needs the letters in order, and most names in a repository lack them.
        if (!HasInOrder(query, name)) return null;

        if (string.Equals(query, name, StringComparison.OrdinalIgnoreCase))
            return new NameMatch(Exact + CaseBonusFor(query, name, 0), Range(0, query.Length));

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return new NameMatch(Prefix + CaseBonusFor(query, name, 0), Range(0, query.Length));

        if (Humps(query, name) is { } humps)
            return new NameMatch(humps[0] == 0 ? HumpsFromStart : HumpsInside, humps);

        var at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
            return new NameMatch(Substring + CaseBonusFor(query, name, at), Range(at, query.Length));

        return InOrder(query, name) is { } letters ? new NameMatch(Subsequence, letters) : null;
    }

    private static int CaseBonusFor(string query, string name, int at) =>
        string.CompareOrdinal(name, at, query, 0, query.Length) == 0 ? CaseBonus : 0;

    private static int[] Range(int start, int length)
    {
        var positions = new int[length];
        for (var i = 0; i < length; i++) positions[i] = start + i;
        return positions;
    }

    /// <summary>
    /// The query as the leading letters of the name's words, in order: each query letter either
    /// carries on the word the previous one was in, or starts a later word. Null when it cannot.
    /// </summary>
    private static int[]? Humps(string query, string name)
    {
        var starts = HumpStarts(name);
        var positions = new int[query.Length];
        HashSet<(int, int)>? failed = null;
        return Walk(0, 0) ? positions : null;

        bool Walk(int qi, int next)
        {
            if (qi == query.Length) return true;
            if (failed is not null && failed.Contains((qi, next))) return false;

            if (qi > 0 && next < name.Length && SameLetter(name[next], query[qi]))
            {
                positions[qi] = next;
                if (Walk(qi + 1, next + 1)) return true;
            }

            for (var h = next + (qi > 0 ? 1 : 0); h < name.Length; h++)
            {
                if (!starts[h] || !SameLetter(name[h], query[qi])) continue;
                positions[qi] = h;
                if (Walk(qi + 1, h + 1)) return true;
            }

            (failed ??= []).Add((qi, next));
            return false;
        }
    }

    /// <summary>Where each word of an identifier begins: the first letter, an upper-case letter after a
    /// lower-case one or a digit, the last capital of an acronym before a lower-case letter, and a
    /// letter or digit after a separator.</summary>
    private static bool[] HumpStarts(string name)
    {
        var starts = new bool[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c)) continue;
            if (i == 0) { starts[i] = true; continue; }

            var previous = name[i - 1];
            starts[i] =
                !char.IsLetterOrDigit(previous)
                || (char.IsUpper(c) && (char.IsLower(previous) || char.IsDigit(previous)))
                || (char.IsUpper(c) && char.IsUpper(previous) && i + 1 < name.Length && char.IsLower(name[i + 1]))
                || (char.IsDigit(c) && char.IsLetter(previous));
        }

        return starts;
    }

    private static int[]? InOrder(string query, string name)
    {
        var positions = new int[query.Length];
        var qi = 0;
        for (var i = 0; i < name.Length && qi < query.Length; i++)
        {
            if (!SameLetter(name[i], query[qi])) continue;
            positions[qi++] = i;
        }

        return qi == query.Length ? positions : null;
    }

    private static bool HasInOrder(string query, string name)
    {
        var qi = 0;
        for (var i = 0; i < name.Length && qi < query.Length; i++)
            if (SameLetter(name[i], query[qi])) qi++;
        return qi == query.Length;
    }

    private static bool SameLetter(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
