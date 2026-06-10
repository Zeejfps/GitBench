using System.Collections.Concurrent;

namespace GitBench;

// Supplies the manual per-repo override and repo-id→path resolution the identity resolver needs,
// without it taking a hard dependency on the whole repo registry. RepoRegistry implements this.
public interface IIdentityOverrides
{
    Guid? GetIdentityOverrideByPath(string path);
    string? GetRepoPathById(Guid repoId);
}

// Resolves which Git identity applies to a repo — a manual override, an explicit --local config,
// or a profile matched on the remote host/owner — and produces the `-c key=value` args injected
// into every git invocation for that repo, without ever writing the repo's config. Results are
// memoized per normalized path; the memo flushes on RefsChangedMessage and whenever profiles change.
//
// Compute() reads config through IGitRawConfigReader with inject:false, so the runner never calls
// back into this resolver while resolving.
public sealed class GitIdentityService
{
    private readonly IGitRawConfigReader _git;
    private readonly IdentityProfileService _profiles;
    private readonly IIdentityOverrides? _overrides;
    private readonly ConcurrentDictionary<string, Identity> _memo = new(PathKey.Comparer);

    // Raised after the memo is flushed (profiles changed / refs changed) so the chip refreshes.
    public event Action? Changed;

    public GitIdentityService(
        IGitRawConfigReader git,
        IdentityProfileService profiles,
        IMessageBus bus,
        IIdentityOverrides? overrides = null)
    {
        _git = git;
        _profiles = profiles;
        _overrides = overrides;
        _profiles.Changed += FlushAll;
        bus.Subscribe<RefsChangedMessage>(OnRefsChanged);
    }

    public Identity Resolve(string workingDir)
    {
        var key = PathKey.Normalize(workingDir);
        if (_memo.TryGetValue(key, out var cached)) return cached;
        var computed = Compute(key);
        return computed.Cacheable ? _memo.GetOrAdd(key, computed) : computed;
    }

    public IReadOnlyList<string> ResolvePrefixArgs(string workingDir) => Resolve(workingDir).PrefixArgs;

    // Lock-free read of an already-resolved identity (no git, no Compute). Used by the chip menu so
    // opening it never blocks the UI thread; null until the first Resolve lands or after a flush.
    public Identity? Cached(string workingDir)
        => _memo.TryGetValue(PathKey.Normalize(workingDir), out var id) ? id : null;

    public void FlushAll()
    {
        _memo.Clear();
        Changed?.Invoke();
    }

    public void Flush(string workingDir)
    {
        _memo.TryRemove(PathKey.Normalize(workingDir), out _);
        Changed?.Invoke();
    }

    // A ref change affects only the one repo it names (host/owner and local config are per-repo),
    // so flush just that entry rather than dumping every repo's cache. Falls back to a full flush
    // if the id→path lookup isn't wired or the repo is unknown.
    private void OnRefsChanged(RefsChangedMessage msg)
    {
        var path = _overrides?.GetRepoPathById(msg.RepoId);
        if (path != null) Flush(path);
        else FlushAll();
    }

    private Identity Compute(string path)
    {
        // A manual override is a deliberate choice and wins over everything, including local config.
        if (_overrides?.GetIdentityOverrideByPath(path) is { } id && _profiles.Find(id) is { } chosen)
            return new Identity.FromProfile(chosen);

        // Don't spawn git against an unreadable repo or cache a wrong answer — retry next op.
        if (!_git.IsRepoAvailable(path))
            return new Identity.Pending();

        try
        {
            // An explicit --local identity (set by the terminal or "pin to repo") is honored as-is,
            // injecting nothing so GUI and terminal commits match. Either field set counts.
            var (localName, localEmail) = _git.GetLocalIdentityRaw(path);
            if (localName != null || localEmail != null)
                return new Identity.FromConfig(localName, localEmail);

            var remotes = _git.GetRemoteNamesRaw(path);
            if (remotes.Count == 0)
                return new Identity.NoRemote();

            var remote = remotes.Contains("origin") ? "origin" : remotes[0];
            var url = _git.GetRemoteUrlRaw(path, remote);
            if (url == null || !RemoteUrl.TryGetHostAndOwner(url, out var host, out var owner))
                return new Identity.Unmatched();

            return MatchProfile(host, owner) is { } profile
                ? new Identity.FromProfile(profile)
                : new Identity.Unmatched();
        }
        catch
        {
            // A git read threw (e.g. a held index.lock): transient, don't memoize.
            return new Identity.Pending();
        }
    }

    // Owner-specific rules beat host-only rules, so a profile pinned to one org wins over a
    // catch-all "any repo on this host". An owner hit returns immediately; the first host-only hit
    // is held as a fallback used only if no owner rule matches.
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
}
