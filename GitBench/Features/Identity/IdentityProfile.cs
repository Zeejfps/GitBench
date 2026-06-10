namespace GitBench.Features.Identity;

// A named Git identity the app can apply to a repo automatically. Stored globally (not
// per-repo) in identity-profiles.json; matched to a repo by its remote host/owner via Match.
//
// SshKeyPath/SigningKey are optional. When SshKeyPath is set, applying the profile injects a
// core.sshCommand selecting that key (so a second account's key in ssh-agent can't be offered
// first). SigningKey is only injected when set, so profiles without one don't accidentally turn
// on commit signing (which would make commits fail without a configured signer).
// SigningKeyFormat ("ssh" / "openpgp") is optional and, when set, is emitted as git's gpg.format
// instead of being guessed from the key string — a bare-filename or fingerprint SSH key can't be
// reliably inferred. Null falls back to a best-effort heuristic. JSON-only, like SigningKey.
public sealed record IdentityProfile(
    Guid Id,
    string DisplayName,
    string UserName,
    string UserEmail,
    string? SshKeyPath = null,
    string? SigningKey = null,
    string? SigningKeyFormat = null,
    List<IdentityMatchRule>? Match = null);

// One auto-match rule: a profile claims a repo when the repo's remote host equals Host and,
// if Owner is set, the remote's first path segment (the org/user) equals Owner. Owner null =
// match any repo on that host.
public sealed record IdentityMatchRule(string Host, string? Owner = null);
