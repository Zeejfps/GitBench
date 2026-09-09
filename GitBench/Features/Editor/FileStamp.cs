namespace GitBench.Features.Editor;

/// <summary>What the filesystem says about a file, in the two facts a write to it moves.</summary>
internal readonly record struct FileStamp(long Length, long ModifiedUtcTicks)
{
    /// <summary>The file as it is now, or null where it is absent or unreadable.</summary>
    public static FileStamp? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>One file open for editing, and the file on disk as the application last saw it.</summary>
internal readonly record struct OpenDocument(string Path, FileStamp? KnownOnDisk);

/// <summary>What a fresh look at the file behind an open document settled.</summary>
internal enum DocumentReconciliation
{
    /// <summary>The file is as the application last saw it, or its session has gone.</summary>
    Unchanged,

    /// <summary>The file moved and nothing had been typed into it.</summary>
    Reloaded,

    /// <summary>The file moved under edits that are not on disk.</summary>
    Diverged,
}
