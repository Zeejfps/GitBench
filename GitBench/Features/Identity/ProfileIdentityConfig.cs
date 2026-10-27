using GitBench.Git;

namespace GitBench.Features.Identity;

// The --local config a profile stands for. Kept apart from LocalIdentityConfig so the git side never
// depends on profiles.
public static class ProfileIdentityConfig
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

    // IdentitiesOnly=yes stops ssh-agent offering another account's key first; the path is quoted
    // because git re-splits this value itself, so a spaced path would otherwise break.
    private static string? SshCommandFor(IdentityProfile p)
    {
        var path = Trimmed(p.SshKeyPath);
        return path == null ? null : $"ssh -i \"{IdentityProfileEditing.ExpandHome(path)}\" -o IdentitiesOnly=yes";
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool LooksLikeSshKey(string key)
        => key.StartsWith("ssh-", StringComparison.Ordinal)
            || key.StartsWith('~') || key.Contains('/') || key.Contains('\\');
}
