namespace GitBench;

// Canonical key for a repo working-dir path: full path with trailing separators trimmed, compared
// case-insensitively on Windows. The identity-override map (RepoRegistry) and the resolver memo
// (GitIdentityService) MUST normalize the same way or an override silently fails to match its
// resolved repo — so both go through here rather than each keeping their own copy.
internal static class PathKey
{
    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }
}
