namespace GitBench.Git;

public interface IGitTagOperations
{
    GitOutcome CreateTag(Repo repo, string name, string message, string commitSha, bool pushToAllRemotes);
    // Pushes an existing tag to remoteName, or to every configured remote when it is null.
    GitOutcome PushTag(Repo repo, string name, string? remoteName = null);
    GitOutcome DeleteTag(Repo repo, string name, bool deleteFromRemotes);
}
