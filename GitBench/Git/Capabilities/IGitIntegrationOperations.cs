namespace GitBench.Git;

public interface IGitIntegrationOperations
{
    MergePreviewResult PreviewMerge(Repo repo, string sourceRef);
    MergeLikeOutcome Merge(Repo repo, string sourceRef, MergeStrategy strategy);
    RebasePreviewResult PreviewRebase(Repo repo, string targetRef);
    MergeLikeOutcome Rebase(Repo repo, string targetRef, bool autostash);
    MergeLikeOutcome CherryPick(Repo repo, string commitSha);
    MergeLikeOutcome RevertCommit(Repo repo, string commitSha);
    AbortOutcome AbortOperation(Repo repo, RepoOperationState state, bool forceQuit = false);
    ContinueOutcome ContinueOperation(Repo repo, RepoOperationState state);
    ContinueOutcome SkipOperation(Repo repo, RepoOperationState state);
}
