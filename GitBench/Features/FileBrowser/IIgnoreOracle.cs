namespace GitBench.Features.FileBrowser;

/// <summary>
/// Whether the repository's ignore rules match a batch of paths. Its own seam rather than a method
/// on the tree: resolving ignore rules means asking git, and a tree that shells out is not a tree
/// that can be tested by flattening a scripted directory.
/// </summary>
/// <remarks>
/// Batched because the alternative is one process per entry, and the directories worth looking at
/// with this feature are exactly the ones with four hundred of them. Paths are repo-relative and
/// slash-separated, the shape git speaks, and a directory carries a trailing slash so a
/// directory-only rule can match it without the path having to still be on disk.
/// </remarks>
internal interface IIgnoreOracle
{
    /// <summary>The subset of <paramref name="relativePaths"/> the ignore rules match.</summary>
    IReadOnlySet<string> Ignored(IReadOnlyList<string> relativePaths);
}

/// <summary>An oracle for a place with no ignore rules to ask about — a directory outside any
/// repository, and the tree tests that are not about ignoring.</summary>
internal sealed class NoIgnoreOracle : IIgnoreOracle
{
    public static readonly NoIgnoreOracle Instance = new();

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> Ignored(IReadOnlyList<string> relativePaths) => None;
}
