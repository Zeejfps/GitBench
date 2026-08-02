namespace GitBench.Git;

public interface IGitWorkingTreeOperations
{
    GitOutcome Stage(Repo repo, IReadOnlyList<string> paths);
    GitOutcome Unstage(Repo repo, IReadOnlyList<string> paths);
    GitOutcome ResetToParent(Repo repo, IReadOnlyList<string> paths);
    GitOutcome DiscardChanges(Repo repo, IReadOnlyList<string> paths);
    GitOutcome ApplyPatch(Repo repo, string patch, bool cached, bool reverse);
    GitOutcome Commit(Repo repo, string message, bool amend);
}
