using System.Collections.Concurrent;

namespace GitBench;

// Where a repo's effective identity comes from. Drives both the injected `-c` args and the
// status-bar chip's label, so the two can never disagree.
public enum IdentitySource
{
    NoRemotes,   // fresh repo / no remote — nothing to match on
    NoMatch,     // has a remote but no profile claims it
    RepoConfig,  // repo has an explicit --local user.name/email — honor it, inject nothing
    Profile,     // a profile matched (or was pinned as a manual override)
}

public sealed record ResolvedIdentity(
    IdentitySource Source,
    Guid? ProfileId,
    string? DisplayName,
    string? UserName,
    string? UserEmail,
    IReadOnlyList<string> PrefixArgs)
{
    // A resolution we couldn't complete because the repo wasn't readable right now (unmounted
    // volume, held index.lock, git momentarily failing). Such results are NOT memoized, so the
    // next git op retries instead of pinning the repo to the machine's global identity until the
    // next flush.
    public bool IsTransient { get; init; }

    // The no-identity shape (no profile, no injected args). One definition for the resolver's
    // NoRemotes/NoMatch results, the transient sentinel, and the chip menu's pre-resolve fallback.
    public static ResolvedIdentity Empty(IdentitySource source, bool transient = false)
        => new(source, null, null, null, null, Array.Empty<string>()) { IsTransient = transient };
}

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
    private readonly ConcurrentDictionary<string, ResolvedIdentity> _memo = new(PathKey.Comparer);

    // Optional per-repo manual override: repo path → profile id. Wired in a later step; for now
    // it's empty so resolution is purely automatic.
    private Func<string, Guid?>? _overrideLookup;

    // Repo-id → working-dir path, used to flush only the changed repo on a RefsChangedMessage.
    private Func<Guid, string?>? _repoPathLookup;

    // Raised after the memo is flushed (profiles changed / refs changed) so the chip refreshes.
    public event Action? Changed;

    public GitIdentityService(IGitRawConfigReader git, IdentityProfileService profiles, IMessageBus bus)
    {
        _git = git;
        _profiles = profiles;
        _profiles.Changed += FlushAll;
        bus.Subscribe<RefsChangedMessage>(OnRefsChanged);
    }

    // Lets a later step supply a repo-path → profile-id override map without a constructor cycle.
    public void SetOverrideLookup(Func<string, Guid?> lookup) => _overrideLookup = lookup;

    // Lets startup supply repo-id → path resolution for the targeted RefsChanged flush.
    public void SetRepoPathLookup(Func<Guid, string?> lookup) => _repoPathLookup = lookup;

    // A ref change (commit/push/fetch/remote edit) only affects the identity of the ONE repo it
    // names — host/owner and local config are per-repo — so flush just that repo's memo entry
    // rather than dumping every repo's cache and forcing a workspace-wide re-resolve storm. Falls
    // back to a full flush only if the id→path lookup isn't wired or the repo is unknown.
    private void OnRefsChanged(RefsChangedMessage msg)
    {
        var path = _repoPathLookup?.Invoke(msg.RepoId);
        if (path != null) Flush(path);
        else FlushAll();
    }

    public ResolvedIdentity Resolve(string workingDir)
    {
        var key = PathKey.Normalize(workingDir);
        if (_memo.TryGetValue(key, out var cached)) return cached;
        var computed = Compute(key);
        // A transient result is deliberately not stored, so the next op recomputes.
        return computed.IsTransient ? computed : _memo.GetOrAdd(key, computed);
    }

    public IReadOnlyList<string> ResolvePrefixArgs(string workingDir)
        => Resolve(workingDir).PrefixArgs;

    // Cheap lock-free read of an already-resolved identity (no git, no Compute). Used by the chip
    // menu so opening it never blocks the UI thread; returns false until the first Resolve lands or
    // after a flush, in which case the caller shows a neutral state.
    public bool TryGetCached(string workingDir, out ResolvedIdentity resolved)
        => _memo.TryGetValue(PathKey.Normalize(workingDir), out resolved!);

    public void FlushAll()
    {
        _memo.Clear();
        Changed?.Invoke();
    }

    // Drops a single repo's memoized resolution so its next op recomputes. Changed still fires so
    // the status chip re-resolves the active repo (a no-op for the chip if a different repo flushed).
    public void Flush(string workingDir)
    {
        _memo.TryRemove(PathKey.Normalize(workingDir), out _);
        Changed?.Invoke();
    }

    private ResolvedIdentity Compute(string normalizedPath)
    {
        // 1) A manual override is an explicit user choice from the chip menu — it wins over
        //    everything, including a local user.email already in the repo's config. (Auto-matching
        //    still defers to local config in step 2; only a deliberate pick overrides it.)
        var overrideId = _overrideLookup?.Invoke(normalizedPath);
        if (overrideId is { } oid && _profiles.Find(oid) is { } chosen)
            return Build(chosen);

        // If the repo isn't readable right now (unmounted volume, deleted under us), don't spawn git
        // and don't cache a wrong answer — return transient so the next op retries. This is a
        // filesystem stat, so a genuine non-repo path costs nothing extra (no subprocess).
        if (!_git.IsRepoAvailable(normalizedPath))
            return Transient();

        try
        {
            // 2) No override: honor any explicit --local identity (set by the terminal or by "pin to
            //    repo"), injecting nothing so GUI and terminal commits stay identical. Either field
            //    being set means the repo is deliberately configured — never override a local name.
            var (localName, localEmail) = _git.GetLocalIdentityRaw(normalizedPath);
            if (localName != null || localEmail != null)
            {
                var disp = localEmail != null
                    ? (localName != null ? $"{localName} <{localEmail}>" : localEmail)
                    : localName;
                return new ResolvedIdentity(IdentitySource.RepoConfig, null, disp, localName, localEmail, Array.Empty<string>());
            }

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
        catch
        {
            // A git read threw (e.g. a held index.lock): transient, don't memoize.
            return Transient();
        }
    }

    private static ResolvedIdentity Empty(IdentitySource source)
        => ResolvedIdentity.Empty(source);

    // Looks like NoRemotes to the chip (shows nothing), but is never cached.
    private static ResolvedIdentity Transient()
        => ResolvedIdentity.Empty(IdentitySource.NoRemotes, transient: true);

    private static ResolvedIdentity Build(IdentityProfile p)
        => new(IdentitySource.Profile, p.Id, p.DisplayName, p.UserName, p.UserEmail, BuildPrefixArgs(p));

    // Owner-specific rules beat host-only rules, so a personal profile pinned to one org wins
    // over a catch-all "any repo on this host" profile. Single pass: an owner-specific hit returns
    // immediately; the first host-only hit is held as a fallback used only if no owner rule matches.
    private IdentityProfile? MatchProfile(string host, string? owner)
    {
        IdentityProfile? hostOnly = null;
        foreach (var p in _profiles.Snapshot)
        {
            if (p.Match == null) continue;
            foreach (var r in p.Match)
            {
                if (!string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Owner == null)
                    hostOnly ??= p;
                else if (owner != null && string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
        }
        return hostOnly;
    }

    // Single source of truth for a profile's effective config, shared by injection (BuildPrefixArgs)
    // and "pin to repo" (GitService.PinLocalIdentity) so the two can't diverge on SSH/signing.
    public static LocalIdentityConfig BuildLocalConfig(IdentityProfile p)
    {
        var signingKey = string.IsNullOrWhiteSpace(p.SigningKey) ? null : p.SigningKey.Trim();
        string? format = null;
        if (signingKey != null)
            // The key string alone can't reveal ssh vs openpgp (a bare filename/fingerprint has no
            // path/prefix), so prefer the profile's explicit format and only guess as a fallback.
            format = string.IsNullOrWhiteSpace(p.SigningKeyFormat)
                ? (LooksLikeSshSigningKey(signingKey) ? "ssh" : null)
                : p.SigningKeyFormat.Trim();

        return new LocalIdentityConfig(p.UserName, p.UserEmail, BuildSshCommandValue(p), signingKey, format);
    }

    private static IReadOnlyList<string> BuildPrefixArgs(IdentityProfile p)
    {
        var args = new List<string>(12);
        foreach (var (key, value) in BuildLocalConfig(p).Entries())
            if (value != null) { args.Add("-c"); args.Add($"{key}={value}"); }
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
}

// A profile's effective Git identity expressed as the --local config keys this feature owns.
public sealed record LocalIdentityConfig(
    string UserName,
    string UserEmail,
    string? SshCommand,
    string? SigningKey,
    string? SigningKeyFormat)
{
    // Every --local key this feature manages, paired with the value to set — or null to UNSET, so
    // pin can clear a previous profile's leftover SSH/signing config instead of leaving it behind.
    public IEnumerable<(string Key, string? Value)> Entries()
    {
        yield return ("user.name", UserName);
        yield return ("user.email", UserEmail);
        yield return ("core.sshCommand", SshCommand);
        yield return ("user.signingKey", SigningKey);
        yield return ("commit.gpgsign", SigningKey != null ? "true" : null);
        yield return ("gpg.format", SigningKey != null ? SigningKeyFormat : null);
    }
}
