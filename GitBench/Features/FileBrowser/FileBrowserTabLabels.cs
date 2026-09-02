using GitBench.Infrastructure;
using GitBench.Localization;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// Names the tabs in the file browser's strip: a file's own name, and — only against a tab that
/// would otherwise say the same thing — as much of the directory above it as it takes to tell them
/// apart.
/// </summary>
/// <remarks>
/// <para>
/// A repository full of <c>index.ts</c> or <c>mod.rs</c> is the ordinary case in every language that
/// names files by their role, and a strip of four identical tabs is a strip you have to click
/// through to read. The qualifier is as short as it can be — one directory where one directory
/// settles it — because the point is to distinguish, not to spell out the path; the header below the
/// strip still names the file in full.
/// </para>
/// <para>
/// Positional like the terminal's index, in the sense that it belongs to the strip rather than to
/// the file: closing the tab it was being told apart from puts the plain name back.
/// </para>
/// </remarks>
internal static class FileBrowserTabLabels
{
    // Far past what any qualifier should need. The loop cannot rely on the directories differing:
    // two files with the same name under the same trailing directories on different roots — a
    // detached tab beside one in the working tree — never separate, and the answer for them is the
    // longest qualifier rather than an unbounded walk.
    private const int MaxSegments = 8;

    /// <summary>The label for one tab, read against the strip it sits in.</summary>
    public static string For(Strings strings, IReadOnlyList<FileBrowserTab> tabs, FileBrowserTab tab)
    {
        var clashing = new List<FileBrowserTab>();
        foreach (var other in tabs)
            if (!ReferenceEquals(other, tab) && PathKey.Comparer.Equals(other.Name, tab.Name))
                clashing.Add(other);

        if (clashing.Count == 0) return tab.Name;

        var qualifier = string.Empty;
        for (var depth = 1; depth <= MaxSegments; depth++)
        {
            qualifier = Suffix(tab.Path, depth);
            if (qualifier.Length == 0) break;
            if (clashing.TrueForAll(other => !PathKey.Comparer.Equals(Suffix(other.Path, depth), qualifier)))
                break;
        }

        return qualifier.Length == 0 ? tab.Name : strings.FileBrowserTabQualified(tab.Name, qualifier);
    }

    /// <summary>The last <paramref name="depth"/> directories above a file, slash-separated — all of
    /// them once the path runs out, which is what makes a shallow file's qualifier stop growing.</summary>
    private static string Suffix(string path, int depth)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return string.Empty;

        var segments = directory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return string.Empty;

        var take = Math.Min(depth, segments.Length);
        return string.Join('/', segments[^take..]);
    }
}
