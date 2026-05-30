namespace GitGui;

// Fired when the user clicks a submodule pointer-change row in CommitDetails. The
// receiver should activate the submodule's Repo and (if possible) scroll the history
// view to the commit range FromSha..ToSha so the user sees what moved.
public readonly record struct JumpToSubmoduleCommitMessage(
    Guid SubmoduleRepoId,
    string FromSha,
    string ToSha);
