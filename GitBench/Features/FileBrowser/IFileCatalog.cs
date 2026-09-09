namespace GitBench.Features.FileBrowser;

/// <summary>
/// Every file in the working tree worth offering by name, repo-relative and slash-separated.
/// </summary>
/// <remarks>
/// Its own seam for the reason <see cref="IIgnoreOracle"/> is one: the answer comes from git, and a
/// finder that shells out is not a finder that can be tested against a scripted repository. Read
/// whole rather than a directory at a time — not knowing which directory a file is in is the
/// situation this exists for.
/// </remarks>
internal interface IFileCatalog
{
    IReadOnlyList<string> List();
}

/// <summary>A catalog for a place with no repository to ask, and for the tests that are not about
/// finding anything.</summary>
internal sealed class EmptyFileCatalog : IFileCatalog
{
    public static readonly EmptyFileCatalog Instance = new();

    public IReadOnlyList<string> List() => [];
}
