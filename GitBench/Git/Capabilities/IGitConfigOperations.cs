using GitBench.Features.Identity;

namespace GitBench.Git;

public interface IGitConfigOperations
{
    GitOutcome PinLocalIdentity(Repo repo, LocalIdentityConfig config);
    // Enables core.untrackedCache in the repo's --local config, once, if the filesystem supports it
    // and the user hasn't already set the key. Idempotent and respectful: an existing value (either
    // way) is left as the user left it. Never writes --global.
    GitOutcome ApplyUntrackedCache(Repo repo);
}
