namespace GitBench.Features.Repos;

// `<repo>/.git` is a directory in an ordinary repo and a *gitlink file* in a linked worktree or a
// submodule: one line, `gitdir: <path>`, where the path may be relative to the working tree. Every
// file the watcher classifies — HEAD, packed-refs, refs/, worktrees/, modules/ — lives at that
// target, so assuming `<repo>/.git` is where they are means a worktree or submodule entry never
// classifies its own refs at all.
//
// Read the link rather than shelling out to `git rev-parse --absolute-git-dir`: no process spawn per
// repo while the registry is loading, and it still works when git isn't on PATH.
internal static class RepoGitDir
{
    private const string GitDirKey = "gitdir:";

    // Falls back to `<repo>/.git` for anything unreadable or unrecognised — that is the assumption
    // every caller made before this existed, so a failure here is never worse than the status quo.
    public static string Resolve(string repoPath)
    {
        var dotGit = Path.Combine(repoPath, ".git");
        if (Directory.Exists(dotGit) || !File.Exists(dotGit)) return dotGit;

        try
        {
            foreach (var line in File.ReadLines(dotGit))
            {
                if (!line.StartsWith(GitDirKey, StringComparison.Ordinal)) continue;
                var target = line[GitDirKey.Length..].Trim();
                if (target.Length == 0) break;
                // Submodule links are relative to the working tree, worktree links are absolute, and
                // git writes forward slashes on every platform. GetFullPath settles all three.
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target, repoPath));
            }
        }
        catch
        {
            // An unreadable or malformed link is the fallback case, same as no link at all.
        }
        return dotGit;
    }
}
