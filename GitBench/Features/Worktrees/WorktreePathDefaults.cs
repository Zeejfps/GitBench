namespace GitBench.Features.Worktrees;

/// <summary>
/// Derives where a new worktree defaults to living: beside the repository directory, in a folder
/// named after the repo plus the branch being created, so the dialog opens on a location that is
/// one keystroke from correct instead of empty. The name gets a counter while the folder is
/// already taken, since `git worktree add` refuses a non-empty path.
/// </summary>
internal static class WorktreePathDefaults
{
    private const int MaxCandidates = 100;

    /// <summary>The folder the worktree's own directory is created in: the repository's parent.</summary>
    public static string ParentDirectoryFor(string repoPath)
        => Path.GetDirectoryName(Root(repoPath)) ?? string.Empty;

    /// <summary>
    /// The subfolder of <paramref name="parentDir"/> to create, named for the repo and the branch.
    /// <paramref name="parentDir"/> only decides which names are already taken; pass it empty to
    /// skip that check.
    /// </summary>
    public static string FolderNameFor(string repoPath, string parentDir, string branchName, Func<string, bool> directoryExists)
    {
        var repoName = Path.GetFileName(Root(repoPath));
        if (repoName.Length == 0) return string.Empty;

        var slug = Slug(branchName);
        var baseName = slug.Length > 0 ? $"{repoName}-{slug}" : $"{repoName}-worktree";
        if (parentDir.Trim().Length == 0) return baseName;

        var candidate = baseName;
        for (var n = 2; n < MaxCandidates && Taken(parentDir, candidate, directoryExists); n++)
            candidate = $"{baseName}-{n}";
        return candidate;
    }

    /// <summary>
    /// The closest folder that actually exists at or above <paramref name="path"/> — where the
    /// folder picker should open when the field holds a folder that has not been created yet.
    /// Null when nothing on the chain exists.
    /// </summary>
    public static string? NearestExistingDirectory(string path, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string current;
        try { current = Path.GetFullPath(path.Trim()); }
        catch { return null; }

        while (true)
        {
            if (directoryExists(current)) return current;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) return null;
            current = parent;
        }
    }

    private static bool Taken(string parentDir, string name, Func<string, bool> directoryExists)
    {
        try { return directoryExists(Path.Combine(parentDir.Trim(), name)); }
        catch { return false; }
    }

    private static string Root(string repoPath)
    {
        try { return Path.GetFullPath(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch { return string.Empty; }
    }

    // A branch name is a legal refname, not a legal path segment: `feature/foo` would otherwise
    // nest the worktree a directory deeper than the sibling this is meant to produce.
    private static string Slug(string branchName)
    {
        var chars = new List<char>(branchName.Length);
        foreach (var c in branchName.Trim())
        {
            var keep = char.IsLetterOrDigit(c) || c is '.' or '_' or '-';
            if (!keep && chars.Count == 0) continue;
            var next = keep ? c : '-';
            if (next == '-' && chars.Count > 0 && chars[^1] == '-') continue;
            chars.Add(next);
        }
        while (chars.Count > 0 && (chars[^1] == '-' || chars[^1] == '.')) chars.RemoveAt(chars.Count - 1);
        return new string(chars.ToArray());
    }
}
