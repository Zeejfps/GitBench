using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;

namespace GitBench.Git;

public interface IGitStatusReader
{
    Fetched<LocalChangesSnapshot> GetLocalChanges(Repo repo);
    // Unknown = not a repo; null = the probe failed (caller keeps its last known value).
    GitStatusSummary? GetStatusSummary(Repo repo);
    // The refs-only half of the above, for a caller reacting to something that moved refs and not
    // the working tree. Same null/Unknown contract.
    GitSyncSummary? GetSyncSummary(Repo repo);
    HeadCommitMessage? GetHeadCommitMessage(Repo repo);
    IReadOnlyList<FileChange> GetAmendStagedFiles(Repo repo);
    DetachedHeadReport GetDetachedHeadReport(Repo repo);
    RepoOperationState GetOperationState(Repo repo);
    RepoOperation? GetOperation(Repo repo);
    bool HasUnmergedPaths(Repo repo);
    // The default merge commit message (MERGE_MSG) when a merge is in progress, else null.
    // Used to pre-fill the commit box so committing finishes the merge.
    string? GetMergeMessage(Repo repo);
}
