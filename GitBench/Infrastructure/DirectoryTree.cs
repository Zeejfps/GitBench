namespace GitBench.Infrastructure;

/// <summary>
/// Recursive directory removal that survives what a real working tree holds on Windows: junctions
/// (pnpm's node_modules), read-only files (git's loose objects, pnpm's hardlinked store) and files a
/// scanner or editor happens to have open for a moment. Reports what stopped it instead of throwing,
/// so callers can name the leftovers rather than guessing at an errno.
///
/// This exists because git's own recursive delete gets the first of those wrong twice over: it
/// follows junctions rather than removing them, and it abandons the whole walk at the first entry it
/// can't delete — so one junction left dangling by its own traversal is enough for
/// `git worktree remove` to deregister a worktree and still leave thousands of files behind.
/// </summary>
public static class DirectoryTree
{
    /// <summary>
    /// What survived the delete: the root that was asked for, plus the OS's reason for the entry
    /// that blocked it (which names that entry, unlike the errno git reports against the root).
    /// </summary>
    public sealed record Leftovers(string Path, string Reason);

    private const int Attempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Deletes <paramref name="path"/> and everything under it. Junctions and symlinks are removed
    /// as links — never followed — so a linked-to directory outside the tree is left alone.
    /// </summary>
    /// <returns>Null once nothing remains at <paramref name="path"/> (including when it was already
    /// gone), otherwise the leftovers.</returns>
    public static Leftovers? Delete(string path)
    {
        Exception? failure = null;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return null;

            try
            {
                Directory.Delete(path, recursive: true);
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
                // Read-only is the one cause we can clear ourselves. Anything else is a handle
                // someone else has to drop, so give short-lived ones (indexer, antivirus, a
                // delete still pending on a closing handle) a moment before trying again.
                if (ex is UnauthorizedAccessException) ClearReadOnly(path);
                else if (attempt < Attempts) Thread.Sleep(RetryDelay);
            }
        }

        return new Leftovers(path, failure?.Message ?? "The directory could not be deleted.");
    }

    private static void ClearReadOnly(string dir)
    {
        try
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                // Walking into a junction would take the attribute clearing outside the tree —
                // into the pnpm store, or wherever else the link points.
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                if (entry is DirectoryInfo sub) ClearReadOnly(sub.FullName);
                else if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                    entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        catch
        {
            // Best effort: the retry reports whatever still blocks the delete.
        }
    }
}
