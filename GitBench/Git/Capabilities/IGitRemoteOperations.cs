namespace GitBench.Git;

public interface IGitRemoteOperations
{
    IReadOnlyList<string> GetRemoteNames(Repo repo);
    string? GetRemoteUrl(Repo repo, string remoteName);
    GitOutcome AddRemote(Repo repo, string name, string url);
    GitOutcome EditRemote(Repo repo, string oldName, string newName, string url);
    GitOutcome Push(Repo repo, bool force = false);
    PullOutcome Pull(Repo repo, PullStrategy? strategy = null);
    GitOutcome Fetch(Repo repo);
    // Clones url into targetPath (a not-yet-existing or empty directory). onLine streams git's
    // progress output. On success RepoPath carries the absolute path of the new working tree.
    CloneOutcome Clone(string url, string targetPath, Action<string>? onLine = null);
}
