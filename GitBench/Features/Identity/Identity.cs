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
            LocalIdentityConfig.For(Profile).PrefixArgs();
    }

    // Repo unreadable at resolve time (unmounted volume, held index.lock).
    public sealed record Pending : Identity
    {
        public override bool Cacheable => false;
    }
}

// A profile's effective identity as the --local config keys this feature owns. The single source
// of truth shared by injection (PrefixArgs) and "pin to repo" (GitService.PinLocalIdentity), so
// the two can never diverge on SSH/signing.
public sealed record LocalIdentityConfig(
    string UserName,
    string UserEmail,
    string? SshCommand,
    string? SigningKey,
    string? SigningKeyFormat)
{
    public static LocalIdentityConfig For(IdentityProfile p)
    {
        var signingKey = Trimmed(p.SigningKey);
        // A bare filename/fingerprint can't reveal ssh vs openpgp, so trust the profile's explicit
        // format and only guess as a fallback.
        var format = signingKey == null ? null
            : Trimmed(p.SigningKeyFormat) ?? (LooksLikeSshKey(signingKey) ? "ssh" : null);
        return new(p.UserName, p.UserEmail, SshCommandFor(p), signingKey, format);
    }

    // Every managed key paired with its value — or null to UNSET, so a pin can clear a previous
    // profile's leftover SSH/signing config instead of leaving it behind.
    public IEnumerable<(string Key, string? Value)> Entries()
    {
        yield return ("user.name", UserName);
        yield return ("user.email", UserEmail);
        yield return ("core.sshCommand", SshCommand);
        yield return ("user.signingKey", SigningKey);
        yield return ("commit.gpgsign", SigningKey != null ? "true" : null);
        yield return ("gpg.format", SigningKey != null ? SigningKeyFormat : null);
    }

    public IReadOnlyList<string> PrefixArgs()
    {
        var args = new List<string>();
        foreach (var (key, value) in Entries())
            if (value != null) { args.Add("-c"); args.Add($"{key}={value}"); }
        return args;
    }

    // IdentitiesOnly=yes stops ssh-agent offering another account's key first; the path is quoted
    // because git re-splits this value itself, so a spaced path would otherwise break.
    private static string? SshCommandFor(IdentityProfile p)
    {
        var path = Trimmed(p.SshKeyPath);
        return path == null ? null : $"ssh -i \"{ExpandHome(path)}\" -o IdentitiesOnly=yes";
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool LooksLikeSshKey(string key)
        => key.StartsWith("ssh-", StringComparison.Ordinal)
            || key.StartsWith('~') || key.Contains('/') || key.Contains('\\');

    private static string ExpandHome(string path)
        => path[0] == '~' && (path.Length == 1 || path[1] is '/' or '\\')
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;
}
