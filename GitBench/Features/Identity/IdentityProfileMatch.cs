using GitBench.Git;

namespace GitBench.Features.Identity;

// Which profile claims a remote. Shared by the resolver (matching an already-cloned repo by its
// origin URL) and the clone dialog (pre-selecting an identity from the URL being typed), so the
// identity a clone runs under is the same one the repo resolves to afterwards.
internal static class IdentityProfileMatch
{
    public static IdentityProfile? ForRemoteUrl(IReadOnlyList<IdentityProfile> profiles, string? url)
        => !string.IsNullOrWhiteSpace(url) && RemoteUrl.TryGetHostAndOwner(url, out var host, out var owner)
            ? ForHost(profiles, host, owner)
            : null;

    // Owner-specific rules beat host-only rules, so a profile pinned to one org wins over a
    // catch-all "any repo on this host". An owner hit returns immediately; the first host-only hit
    // is held as a fallback used only if no owner rule matches.
    public static IdentityProfile? ForHost(IReadOnlyList<IdentityProfile> profiles, string host, string? owner)
    {
        IdentityProfile? hostOnly = null;
        foreach (var p in profiles)
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
