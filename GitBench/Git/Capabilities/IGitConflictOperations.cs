namespace GitBench.Git;

public interface IGitConflictOperations
{
    // Per-file conflict resolution. TakeOurs/TakeTheirs check out the chosen side and stage
    // it; MarkResolved stages the working-tree file as-is (manual-edit path). Each returns a
    // ResolveOutcome and broadcasting is left to the caller.
    GitOutcome TakeOurs(Repo repo, string path);
    GitOutcome TakeTheirs(Repo repo, string path);
    // Resolves by keeping both sides: writes ours' content followed by theirs' content and stages.
    GitOutcome TakeBoth(Repo repo, string path);
    GitOutcome MarkResolved(Repo repo, string path);
    // Context for the conflict-resolution UI: the in-progress operation plus the ours/theirs
    // commit metadata and per-side change kind. Returns null when the path isn't conflicted.
    ConflictContext? GetConflictContext(Repo repo, string path);
    // Every unmerged path in the repository, in index order, with what each side did to it. One
    // read for the whole repo — asking per path turns a ten-file conflict into ten processes.
    IReadOnlyList<ConflictedPath> GetConflictedPaths(Repo repo);
    // One unmerged path's three merge stages as text. Null when the path isn't unmerged at all,
    // which is also the caller's is-this-a-conflict precondition.
    ConflictStages? GetConflictStages(Repo repo, string path);
}
