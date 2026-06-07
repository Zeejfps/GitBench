using System.Collections.Concurrent;

namespace GitBench;

// Where a repo's effective identity comes from. Drives both the injected `-c` args and the
// status-bar chip's label, so the two can never disagree.
public enum IdentitySource
{
    NoRemotes,   // fresh repo / no remote — nothing to match on
    NoMatch,     // has a remote but no profile claims it
    RepoConfig,  // repo has an explicit --local user.email — honor it, inject nothing
    Profile,     // a profile matched (or was pinned as a manual override)
}

public sealed record ResolvedIdentity(
    IdentitySource Source,
    Guid? ProfileId,
    string? DisplayName,
    string? UserName,
    string? UserEmail,
    IReadOnlyList<string> PrefixArgs);

// Resolves which Git identity applies to a repo (by its remote host/owner) and produces the
// `-c key=value` args injected into every git invocation for that repo — without ever writing
// the repo's config. Results are memoized per normalized working-dir path; the memo is flushed
// on RefsChangedMessage (push/fetch/remote edits) and whenever profiles change.
//
// Recursion: Compute() reads remote URL + local config through IGitRawConfigReader, which runs
// git with inject:false, so the runner never calls back into this resolver while resolving.
public sealed class GitIdentityService
{
    private readonly IGitRawConfigReader _git;
    private readonly IdentityProfileService _profiles;
    private readonly ConcurrentDictionary<string, ResolvedIdentity> _memo =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // Optional per-repo manual override: repo path → profile id. Wired in a later step; for now
    // it's empty so resolution is purely automatic.
    private Func<string, Guid?>? _overrideLookup;

    // Raised after the memo is flushed (profiles changed / refs changed) so the chip refreshes.
    public event Action? Changed;

    public GitIdentityService(IGitRawConfigReader git, IdentityProfileService profiles, IMessageBus bus)
    {
        _git = git;
        _profiles = profiles;
        _profiles.Changed += FlushAll;
        bus.Subscribe<RefsChangedMessage>(_ => FlushAll());
    }

    // Lets a later step supply a repo-path → profile-id override map without a constructor cycle.
    public void SetOverrideLookup(Func<string, Guid?> lookup) => _overrideLookup = lookup;

    public ResolvedIdentity Resolve(string workingDir)
        => _memo.GetOrAdd(NormalizeKey(workingDir), Compute);

    public IReadOnlyList<string> ResolvePrefixArgs(string workingDir)
        => Resolve(workingDir).PrefixArgs;

    public void FlushAll()
    {
        _memo.Clear();
        Changed?.Invoke();
    }

    private ResolvedIdentity Compute(string normalizedPath)
    {
        // 1) An explicit --local user.email wins outright: honor it, inject nothing. This keeps
        //    GUI and terminal commits identical and makes "pin to repo" coherent.
        var (localName, localEmail) = _git.GetLocalIdentityRaw(normalizedPath);
        if (localEmail != null)
        {
            var disp = localName != null ? $"{localName} <{localEmail}>" : localEmail;
            return new ResolvedIdentity(IdentitySource.RepoConfig, null, disp, localName, localEmail, Array.Empty<string>());
        }

        // 2) Manual override (pin) takes precedence over auto-match.
        var overrideId = _overrideLookup?.Invoke(normalizedPath);
        if (overrideId is { } oid && FindProfile(oid) is { } pinned)
            return Build(pinned);

        // 3) Auto-match on the remote host/owner.
        var remotes = _git.GetRemoteNamesRaw(normalizedPath);
        if (remotes.Count == 0)
            return Empty(IdentitySource.NoRemotes);

        var remoteName = remotes.Contains("origin") ? "origin" : remotes[0];
        var url = _git.GetRemoteUrlRaw(normalizedPath, remoteName);
        if (url == null || !RemoteUrl.TryGetHostAndOwner(url, out var host, out var owner))
            return Empty(IdentitySource.NoMatch);

        var profile = MatchProfile(host, owner);
        return profile != null ? Build(profile) : Empty(IdentitySource.NoMatch);
    }

    private static ResolvedIdentity Empty(IdentitySource source)
        => new(source, null, null, null, null, Array.Empty<string>());

    private static ResolvedIdentity Build(IdentityProfile p)
        => new(IdentitySource.Profile, p.Id, p.DisplayName, p.UserName, p.UserEmail, BuildPrefixArgs(p));

    private IdentityProfile? FindProfile(Guid id)
    {
        foreach (var p in _profiles.Profiles)
            if (p.Id == id) return p;
        return null;
    }

    // Owner-specific rules beat host-only rules, so a personal profile pinned to one org wins
    // over a catch-all "any repo on this host" profile.
    private IdentityProfile? MatchProfile(string host, string? owner)
    {
        if (owner != null)
            foreach (var p in _profiles.Profiles)
                if (p.Match != null)
                    foreach (var r in p.Match)
                        if (r.Owner != null
                            && string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase))
                            return p;

        foreach (var p in _profiles.Profiles)
            if (p.Match != null)
                foreach (var r in p.Match)
                    if (r.Owner == null && string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase))
                        return p;

        return null;
    }

    private static IReadOnlyList<string> BuildPrefixArgs(IdentityProfile p)
    {
        var args = new List<string>(8);
        void Add(string kv) { args.Add("-c"); args.Add(kv); }

        Add($"user.name={p.UserName}");
        Add($"user.email={p.UserEmail}");

        if (BuildSshCommandValue(p) is { } ssh)
            Add($"core.sshCommand={ssh}");

        // Only enable signing when a key is set — turning on gpgsign without a configured signer
        // would make every commit fail.
        if (!string.IsNullOrWhiteSpace(p.SigningKey))
        {
            var sk = p.SigningKey.Trim();
            Add($"user.signingKey={sk}");
            Add("commit.gpgsign=true");
            if (LooksLikeSshSigningKey(sk)) Add("gpg.format=ssh");
        }

        return args;
    }

    // The core.sshCommand value for a profile, or null if it has no SSH key. IdentitiesOnly=yes
    // stops ssh-agent offering the other account's key first; the key path is double-quoted
    // because git re-shell-splits this value itself, so a spaced path would otherwise break.
    // Shared by injection and by "pin to repo" so both produce the identical command.
    public static string? BuildSshCommandValue(IdentityProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.SshKeyPath)) return null;
        var key = ExpandHome(p.SshKeyPath.Trim());
        return $"ssh -i \"{key}\" -o IdentitiesOnly=yes";
    }

    private static bool LooksLikeSshSigningKey(string key)
        => key.StartsWith("ssh-", StringComparison.Ordinal)
            || key.StartsWith("~", StringComparison.Ordinal)
            || key.Contains('/')
            || key.Contains('\\');

    private static string ExpandHome(string path)
    {
        if (path.Length > 0 && (path[0] == '~') && (path.Length == 1 || path[1] == '/' || path[1] == '\\'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home + path[1..];
        }
        return path;
    }

    private static string NormalizeKey(string repoPath)
    {
        try { return Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return repoPath; }
    }
}
