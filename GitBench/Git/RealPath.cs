namespace GitBench.Git;

/// <summary>
/// A path with every symlink along it followed, so that two spellings of one location compare equal.
/// </summary>
/// <remarks>
/// git prints paths it has already resolved — a repo under <c>/var/folders/…</c> on macOS comes back
/// from <c>git worktree list</c> as <c>/private/var/folders/…</c>, because <c>/var</c> is a symlink —
/// while the app holds whatever path the user picked. Comparing git's answer against our own needs
/// both sides put through the same resolution. <see cref="FileSystemInfo.ResolveLinkTarget"/> follows
/// only a link that is the path's last segment, so the walk has to go a segment at a time.
/// </remarks>
internal static class RealPath
{
    public static string Of(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            var resolved = root;
            foreach (var segment in full[root.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                resolved = Follow(Path.Combine(resolved, segment));
            }
            return TrimTrailingSeparator(resolved);
        }
        catch
        {
            return TrimTrailingSeparator(path);
        }
    }

    // A segment that isn't on disk can't be a link, and one we can't stat stays as it is: this
    // resolves paths for comparison, and a path nothing can be learned about still compares as
    // itself.
    private static string Follow(string path)
    {
        try
        {
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path)) return path;

            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? Path.GetFullPath(target.FullName)
                : path;
        }
        catch
        {
            return path;
        }
    }

    private static string TrimTrailingSeparator(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
