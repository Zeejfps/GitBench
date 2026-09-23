using GitBench.Infrastructure;

namespace GitBench.Features.FileBrowser;

/// <summary>Finding files in a working tree by name: the listing and the ranking that find file and
/// search everywhere both answer from.</summary>
internal static class WorkingTreeFileSearch
{
    /// <summary>Every file to search, or none when listing them failed. Slow: run it off the UI thread.</summary>
    public static IReadOnlyList<string> List(Func<IReadOnlyList<string>> listFiles)
    {
        try
        {
            return listFiles();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileBrowser] Listing the files to search failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>The paths the query reaches, best first, at most <paramref name="max"/> of them.</summary>
    public static FileFinderResults Rank(IReadOnlyList<string> listed, string query, int max)
    {
        // One past the cap, so "there are more" is known without ranking the repository twice.
        var ranked = PathSearch.Rank(listed, query, max + 1);
        var truncated = ranked.Count > max;
        var paths = new string[truncated ? max : ranked.Count];
        for (var i = 0; i < paths.Length; i++) paths[i] = ranked[i].Path;
        return new FileFinderResults(paths, truncated);
    }
}
