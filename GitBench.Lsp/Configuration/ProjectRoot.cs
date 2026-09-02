namespace GitBench.Lsp.Configuration;

/// <summary>
/// Finds the directory a server should be launched in, by walking up from a file looking for the
/// markers its config names. Nested projects and submodules work because the walk stops at the
/// first marker it meets, not the last.
/// </summary>
public static class ProjectRoot
{
    /// <summary>
    /// The project root for <paramref name="filePath"/>, or none when the file is not inside the
    /// repository. With no markers configured, or none found, the repository root is the root —
    /// a language with no project file still gets a server.
    /// </summary>
    /// <remarks>
    /// The walk never leaves the repository. A Cargo.toml in the user's home directory is not the
    /// project root of a file in a repository below it, and treating it as one would hand a server
    /// a workspace containing everything they own.
    /// </remarks>
    public static string? Find(string repoRoot, string filePath, IReadOnlyList<string> markers)
    {
        var root = Normalize(repoRoot);
        var directory = Path.GetDirectoryName(Normalize(filePath));
        if (directory is null || !IsInside(root, directory)) return null;
        if (markers.Count == 0) return root;

        var current = directory;
        while (true)
        {
            foreach (var marker in markers)
            {
                var candidate = Path.Combine(current, marker);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return current;
            }

            if (PathsEqual(current, root)) return root;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Length == current.Length) return root;
            current = parent;
        }
    }

    static bool IsInside(string root, string directory) =>
        PathsEqual(root, directory) ||
        directory.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, Comparison);

    static bool PathsEqual(string a, string b) => string.Equals(a, b, Comparison);

    static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
