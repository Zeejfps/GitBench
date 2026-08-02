namespace GitBench.Git;

public interface IGitStashOperations
{
    GitOutcome CreateStash(Repo repo, string message, bool includeUntracked, bool keepIndex, IReadOnlyList<string> paths);
    MergeLikeOutcome ApplyStash(Repo repo, int index);
    GitOutcome DropStash(Repo repo, int index);
    GitOutcome RenameStash(Repo repo, int index, string newMessage);
}
