namespace GitBench.Infrastructure;

// Crash-safe text write: serialize to a sibling .tmp then atomically rename over the target, so a
// crash mid-write can't leave a truncated file that fails to parse on next launch (silently
// resetting state). File.Move with overwrite is an atomic rename on the same volume. Shared by
// every JSON store (preferences, repo state, identity profiles) so none can drift back to a plain
// non-atomic write.
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
