using GitBench.Features.Submodules;

namespace GitBench.Git;

public interface IGitSubmoduleOperations
{
    IReadOnlyList<SubmoduleInfo> ListSubmodules(Repo primary);
    GitOutcome AddSubmodule(Repo primary, SubmoduleAddRequest request);
    MergeLikeOutcome UpdateSubmodules(Repo primary, SubmoduleUpdateRequest request);
    GitOutcome DeinitSubmodule(Repo primary, string submodulePath, bool force);
    // Stages the parent's gitlink for a submodule whose HEAD has moved, so the pointer update
    // becomes a deliberate staged change instead of a lingering unstaged "modified" entry.
    // relativePath is the submodule's path within parent's working tree. Returns true when the
    // recorded pointer differed and was staged; false when it was already in sync (a no-op).
    bool StageSubmodulePointer(Repo parent, string relativePath);
    IReadOnlyList<SubmodulePointerChange> GetSubmodulePointerChanges(Repo repo, string commitSha);
}
