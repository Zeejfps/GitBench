namespace GitBench.Tests;

/// <summary>A file browser with no ignore rules and no repository to list, for the tests that are
/// not about ignoring or finding anything.</summary>
internal static class FileBrowserFakes
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    public static IReadOnlySet<string> NoIgnore(IReadOnlyList<string> relativePaths) => None;

    public static IReadOnlyList<string> EmptyCatalog() => [];
}
