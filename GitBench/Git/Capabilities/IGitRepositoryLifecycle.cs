namespace GitBench.Git;

public interface IGitRepositoryLifecycle
{
    // Runs `git init` in path, creating the directory if it doesn't exist yet. Re-initializing a
    // folder that is already a repository succeeds and leaves it alone, which is what git does.
    GitOutcome Init(string path);
}
