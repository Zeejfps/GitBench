namespace GitBench.Features.FileBrowser;

/// <summary>
/// One entry of a directory, as the filesystem reports it. <see cref="IsDirectory"/> follows a link
/// to its target, so a link to a directory reads as a directory here and the browser can offer to
/// open it; <see cref="IsLink"/> is what stops the tree from walking into one blindly.
/// </summary>
internal readonly record struct FileSystemEntry(string Name, bool IsDirectory, bool IsLink, bool IsHidden);

/// <summary>
/// What asking for a directory's contents produced. A directory that vanished between being listed
/// in its parent and being expanded, and one the process may not read, are the same answer to the
/// tree — it has no children to draw — and neither is an exception the caller has to remember to
/// catch.
/// </summary>
internal abstract record DirectoryListing
{
    public sealed record Listed(IReadOnlyList<FileSystemEntry> Entries) : DirectoryListing;

    /// <summary>The directory could not be read, with the OS's own words for why.</summary>
    public sealed record Unavailable(string Reason) : DirectoryListing;

    public static readonly DirectoryListing Empty = new Listed([]);
}

/// <summary>
/// The disk, as the file browser sees it: one directory at a time, never recursively. The seam
/// exists so the flattening can be tested without a temp directory, and so the real read can be
/// moved off the UI thread and cancelled without the tree knowing about either.
/// </summary>
internal interface IFileSystemReader
{
    DirectoryListing List(string absoluteDirectory, CancellationToken cancellation);

    /// <summary>
    /// Where a link finally points, fully resolved, or null when the path is not a link (or the
    /// chain cannot be followed). The tree compares this against the directories already on the
    /// path from the root, which is how <c>a -&gt; ..</c> gets listed without becoming infinite.
    /// </summary>
    string? ResolveLinkTarget(string absolutePath);
}
