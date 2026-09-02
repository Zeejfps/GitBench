namespace GitBench.Features.Worktrees;

/// <summary>
/// Derives the path a new worktree defaults to: a sibling of the repository directory, named
/// after the repo plus the branch being created, so the dialog opens on a path that is one
/// keystroke from correct instead of empty. Suffixed with a counter while the directory is
/// already taken, since `git worktree add` refuses a non-empty path.
/// </summary>
internal static class WorktreePathDefaults
{
    private const int MaxCandidates = 100;

    public static string For(string repoPath, string branchName, Func<string, bool> directoryExists)
    {
        string root;
        try { root = Path.GetFullPath(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch { return string.Empty; }

        var parent = Path.GetDirectoryName(root);
        var repoName = Path.GetFileName(root);
        if (string.IsNullOrEmpty(parent) || repoName.Length == 0) return string.Empty;

        var slug = Slug(branchName);
        var baseName = slug.Length > 0 ? $"{repoName}-{slug}" : $"{repoName}-worktree";

        var candidate = Path.Combine(parent, baseName);
        for (var n = 2; n < MaxCandidates && directoryExists(candidate); n++)
            candidate = Path.Combine(parent, $"{baseName}-{n}");
        return candidate;
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
