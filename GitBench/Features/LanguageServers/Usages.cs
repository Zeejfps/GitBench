using GitBench.Lsp.Documents;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// The source line behind a usage, or the absence of one. A sum rather than a nullable string
/// because "read it and it was blank" and "there was nothing to read" reach the row the same way
/// and must render the same way — a location with no code beside it — and neither is an error.
/// </summary>
internal abstract record UsageText
{
    private UsageText() { }

    public static readonly UsageText Unavailable = new Unreadable();

    public sealed record Unreadable : UsageText;

    public sealed record Source(string Text) : UsageText;
}

/// <summary>One place a symbol is used: where picking it goes, and what the line there says.</summary>
internal sealed record UsageSite(string AbsolutePath, int Line, string ShownPath, UsageText Text)
{
    /// <summary>The location as a reader reads one. Derived rather than stored, so the label and the
    /// line a pick navigates to cannot drift apart.</summary>
    public string Where => $"{ShownPath}:{Line}";
}

/// <summary>
/// The usages to show for a symbol: all of them, or a prefix of a longer list. A sum rather than a
/// list plus a flag because the count of rows and the count of usages are then two different
/// numbers that both have to be carried, and a reader shown 100 of 412 has to be told which is
/// which.
/// </summary>
internal abstract record UsageList
{
    private UsageList() { }

    public static readonly UsageList None = new All([]);

    public sealed record All(IReadOnlyList<UsageSite> Sites) : UsageList;

    public sealed record Capped(IReadOnlyList<UsageSite> Sites, int Total) : UsageList;
}

/// <summary>Turns the sites a language server named into rows a reader can pick from.</summary>
internal static class Usages
{
    // Past this many rows the list has stopped being something to read and the filter box is the
    // only way through it anyway, so the tail buys nothing for the files it would have to open.
    public const int Limit = 100;

    // A minified bundle or a generated file is one line long and thousands of columns wide, and the
    // menu measures its width from the widest row it is given.
    private const int MaxSourceLength = 160;

    public static IReadOnlyList<UsageSite> SitesOf(UsageList usages) => usages switch
    {
        UsageList.All all => all.Sites,
        UsageList.Capped capped => capped.Sites,
        _ => throw new NotSupportedException($"unhandled usage list {usages.GetType().Name}"),
    };

    /// <summary>
    /// The rows for a set of sites, with the source line each one sits on read from
    /// <paramref name="readLines"/> — which answers null for a file there is nothing to read from.
    /// Sites are ordered by file and then by line, so the files open one after another and each is
    /// read exactly once however many usages it holds.
    /// </summary>
    public static UsageList From(
        string repoRoot,
        IReadOnlyList<DefinitionTarget> sites,
        Func<string, IReadOnlyList<string>?> readLines)
    {
        if (sites.Count == 0) return UsageList.None;

        var located = new List<(string AbsolutePath, string ShownPath, int Line)>(sites.Count);
        foreach (var site in sites) located.Add(Locate(repoRoot, site));
        located.Sort(static (a, b) =>
        {
            var byPath = string.CompareOrdinal(a.ShownPath, b.ShownPath);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });

        // Capped before anything is read: the rows past the cap are never shown, and opening their
        // files to fill in text nobody sees is the whole cost of a symbol used everywhere.
        var shown = located.Count > Limit ? located.GetRange(0, Limit) : located;

        var rows = new List<UsageSite>(shown.Count);
        string? open = null;
        IReadOnlyList<string>? lines = null;
        foreach (var at in shown)
        {
            if (open != at.AbsolutePath)
            {
                open = at.AbsolutePath;
                lines = readLines(at.AbsolutePath);
            }

            rows.Add(new UsageSite(at.AbsolutePath, at.Line, at.ShownPath, TextOn(lines, at.Line)));
        }

        return located.Count > Limit ? new UsageList.Capped(rows, located.Count) : new UsageList.All(rows);
    }

    /// <summary>
    /// What the line says, with its indentation dropped so a deeply nested usage reads beside a
    /// top-level one. A line the file does not have is not an error to report: the server indexed a
    /// version of the file that has since been edited, and the location is still worth offering.
    /// </summary>
    private static UsageText TextOn(IReadOnlyList<string>? lines, int line)
    {
        if (lines is null) return UsageText.Unavailable;
        if (line < 1 || line > lines.Count) return UsageText.Unavailable;

        var text = lines[line - 1].Trim();
        // Bytes read out of something that was never text at all. Showing them would put control
        // characters through the menu's own text layout.
        if (text.Length == 0 || text.Contains('\0')) return UsageText.Unavailable;

        return new UsageText.Source(
            text.Length > MaxSourceLength ? text[..MaxSourceLength] + "…" : text);
    }

    private static (string AbsolutePath, string ShownPath, int Line) Locate(
        string repoRoot, DefinitionTarget site) =>
        site switch
        {
            DefinitionTarget.InRepo inside => (
                Path.Combine(repoRoot, inside.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                inside.RelativePath,
                inside.Position.Line.ToOneBased()),
            // Shown whole: a usage in a package cache or a standard library is somewhere the reader
            // has no mental root for, and the tail of the path alone would name several files.
            DefinitionTarget.OutsideRepo outside => (
                outside.AbsolutePath, outside.AbsolutePath, outside.Position.Line.ToOneBased()),
            _ => throw new NotSupportedException($"unhandled usage site {site.GetType().Name}"),
        };
}
