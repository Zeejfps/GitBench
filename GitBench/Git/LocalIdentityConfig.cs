namespace GitBench.Git;

// A repo's Git identity as the --local config keys the app manages. The single source
// of truth shared by injection (PrefixArgs) and "pin to repo" (GitService.PinLocalIdentity), so
// the two can never diverge on SSH/signing.
public sealed record LocalIdentityConfig(
    string UserName,
    string UserEmail,
    string? SshCommand,
    string? SigningKey,
    string? SigningKeyFormat)
{
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
}
