namespace GitBench.Features.FileBrowser;

/// <summary>The real disk behind <see cref="IFileSystemReader"/>.</summary>
/// <remarks>
/// Hidden and system entries are deliberately not skipped — the default enumeration drops both, and
/// the dotfile a build wrote is precisely what someone opens this to find. Links are reported as
/// links and never followed here; where one leads is a separate question, asked only when the tree
/// is about to walk into it.
/// </remarks>
internal sealed class FileSystemReader : IFileSystemReader
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public DirectoryListing List(string absoluteDirectory, CancellationToken cancellation)
    {
        try
        {
            var entries = new List<FileSystemEntry>();
            foreach (var info in new DirectoryInfo(absoluteDirectory).EnumerateFileSystemInfos("*", Options))
            {
                cancellation.ThrowIfCancellationRequested();
                var attributes = info.Attributes;
                entries.Add(new FileSystemEntry(
                    info.Name,
                    info is DirectoryInfo,
                    (attributes & FileAttributes.ReparsePoint) != 0,
                    (attributes & FileAttributes.Hidden) != 0 || info.Name.StartsWith('.')));
            }

            return new DirectoryListing.Listed(entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new DirectoryListing.Unavailable(ex.Message);
        }
    }

    public string? ResolveLinkTarget(string absolutePath)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(absolutePath)
                ? new DirectoryInfo(absolutePath)
                : new FileInfo(absolutePath);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
