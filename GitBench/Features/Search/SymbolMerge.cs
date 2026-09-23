using GitBench.Lsp;

namespace GitBench.Features.Search;

/// <summary>Which of the two sources a symbol row came from.</summary>
internal enum SymbolSource
{
    Index,
    Server,
}

/// <summary>A symbol as search lists it: the row, where it came from, and how well it matched.</summary>
internal sealed record SymbolHit(SymbolRow Row, SymbolSource Source, int Score, IReadOnlyList<int> Highlights);

/// <summary>
/// Puts the index's rows and the language servers' answers into one list, as a pure function of
/// the two, so the order answers arrive in cannot change the result.
/// </summary>
/// <remarks>
/// Priority is per language. Once a server for a language has answered, its rows lead for that
/// language's files, and the index's rows it also returned — same file, same name, name within a
/// line — are dropped. The index's rows it did not return stay, below all of the server-first rows:
/// servers cap and filter their answers each their own way, and dropping those would hide real
/// definitions. Rows of a language no server answered for rank as they would alone.
/// </remarks>
internal static class SymbolMerge
{
    /// <summary>What a server row scores when the app's own matcher would not have matched it: the
    /// server's matching is its own, and its answer counts.</summary>
    private const int ServerOnlyScore = 1;

    public static IReadOnlyList<SymbolHit> Merge(
        IReadOnlyList<RankedSymbol> index,
        IReadOnlyDictionary<LanguageId, IReadOnlyList<SymbolRow>> answers,
        SymbolQuery query,
        Func<string, LanguageId?> languageOf,
        Func<SymbolRow, bool> include,
        int limit)
    {
        var first = new List<SymbolHit>();
        var below = new List<SymbolHit>();
        var served = new Dictionary<(string Path, string Name), List<int>>();

        foreach (var (_, rows) in answers)
        {
            foreach (var row in rows)
            {
                if (!include(row)) continue;
                var match = SymbolSearch.Score(query, row);
                first.Add(new SymbolHit(row, SymbolSource.Server, match?.Score ?? ServerOnlyScore, match?.Highlights ?? []));

                var key = (row.Path, row.Name);
                if (!served.TryGetValue(key, out var lines)) served[key] = lines = [];
                lines.Add(row.Line.Value);
            }
        }

        foreach (var ranked in index)
        {
            if (!include(ranked.Row)) continue;
            var hit = new SymbolHit(ranked.Row, SymbolSource.Index, ranked.Score, ranked.Highlights);
            if (languageOf(ranked.Row.Path) is not { } language || !answers.ContainsKey(language))
            {
                first.Add(hit);
                continue;
            }

            if (!ServerReturned(served, ranked.Row)) below.Add(hit);
        }

        first.Sort(Compare);
        below.Sort(Compare);
        first.AddRange(below);
        return first.Count > limit ? first.GetRange(0, limit) : first;
    }

    private static bool ServerReturned(Dictionary<(string Path, string Name), List<int>> served, SymbolRow row)
    {
        if (!served.TryGetValue((row.Path, row.Name), out var lines)) return false;
        foreach (var line in lines)
            if (Math.Abs(line - row.Line.Value) <= 1)
                return true;
        return false;
    }

    private static int Compare(SymbolHit a, SymbolHit b) => SymbolSearch.Compare(a.Row, a.Score, b.Row, b.Score);
}
