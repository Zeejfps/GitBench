namespace GitBench.Features.Identity;

// The Git identity resolved for a repo: a closed set of outcomes, each carrying exactly the data
// that outcome has. Replaces a flag-bag of (enum + nullable fields + bool) where nonsense such as
// a "matched profile" with no profile, or a cached "couldn't read the repo", was representable.
public abstract record Identity
{
    private Identity() { }

    // `-c key=value` overrides prepended to git for this repo; non-empty only for FromProfile.
    public virtual IReadOnlyList<string> PrefixArgs => Array.Empty<string>();

    // Pending is the lone un-cacheable case: the repo wasn't readable, so the next op recomputes
    // instead of pinning a wrong answer until the next flush.
    public virtual bool Cacheable => true;

    // Fresh repo / no remote: nothing to match on.
    public sealed record NoRemote : Identity;

    // Has a remote, but no profile claims its host/owner.
    public sealed record Unmatched : Identity;

    // The repo carries an explicit --local user.name/email; honor it and inject nothing.
    public sealed record FromConfig(string? UserName, string? UserEmail) : Identity;

    // A profile matched by remote, or was pinned from the chip menu.
    public sealed record FromProfile(IdentityProfile Profile) : Identity
    {
        public override IReadOnlyList<string> PrefixArgs { get; } =
            ProfileIdentityConfig.For(Profile).PrefixArgs();
    }

    // Repo unreadable at resolve time (unmounted volume, held index.lock).
    public sealed record Pending : Identity
    {
        public override bool Cacheable => false;
    }
}
