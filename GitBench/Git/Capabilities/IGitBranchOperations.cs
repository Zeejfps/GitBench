using GitBench.Features.Branches;

namespace GitBench.Git;

public interface IGitBranchOperations
{
    Fetched<BranchListing> GetBranches(Repo repo);
    // startPoint is a GitRef rather than a string so "from the current branch" is expressed as
    // GitRef.Head and resolved here, under the lock — never as a branch name the UI captured.
    GitOutcome CreateBranch(Repo repo, string name, GitRef startPoint, bool checkout);
    GitOutcome RenameBranch(Repo repo, string oldName, string newName, bool force);
    GitOutcome DeleteBranch(Repo repo, string name, bool force);
    GitOutcome DeleteRemoteBranch(Repo repo, string remoteName, string branchName);
    GitOutcome MoveBranch(Repo repo, string branchName, string commitSha, bool checkout);
    GitOutcome CheckoutLocalBranch(Repo repo, string branchName);
    GitOutcome CheckoutRemoteBranch(Repo repo, string localName, string remoteName, string remoteBranchName, bool track);
    GitOutcome FastForwardBranch(Repo repo, string localBranch, string remoteName, string remoteBranch, Action<string>? onLine = null);
    GitOutcome PublishBranch(Repo repo, string localBranch, string remoteName, string remoteBranchName, bool setUpstream);
    GitOutcome AttachDetachedHead(Repo repo, string branch);
    GitOutcome ResetCurrent(Repo repo, string commitSha, ResetMode mode);
}
