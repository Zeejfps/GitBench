namespace GitBench.Features.Identity;

// Shared editing helpers behind the add/edit form and the profile manager: turn the six editable
// fields into an IdentityProfile, and locate a sensible starting folder for the SSH-key picker.
// Kept in one place so both editors build profiles identically.
internal static class IdentityProfileEditing
{
    // Builds a profile from the form fields. When editing, the id, signing key, and any match rules
    // beyond the first (which power users add by hand in identity-profiles.json) are preserved so
    // saving an unrelated field doesn't silently destroy them.
    public static IdentityProfile Build(
        IdentityProfile? existing,
        string displayName,
        string authorName,
        string authorEmail,
        string sshKeyPath,
        string matchHost,
        string matchOwner)
    {
        var host = matchHost.Trim();
        var owner = matchOwner.Trim();
        var match = new List<IdentityMatchRule>();
        if (host.Length > 0)
            match.Add(new IdentityMatchRule(host, owner.Length > 0 ? owner : null));
        if (existing?.Match is { Count: > 1 } existingRules)
            for (var i = 1; i < existingRules.Count; i++)
                match.Add(existingRules[i]);

        var sshKey = sshKeyPath.Trim();
        return new IdentityProfile(
            existing?.Id ?? Guid.NewGuid(),
            displayName.Trim(),
            authorName.Trim(),
            authorEmail.Trim(),
            sshKey.Length > 0 ? sshKey : null,
            existing?.SigningKey,
            existing?.SigningKeyFormat,
            Match: match);
    }

    // Where the SSH-key file picker should open: the folder of the current value if it exists,
    // otherwise the user's ~/.ssh folder when present. Null lets the OS choose its default.
    public static string? InitialSshKeyDirectory(string sshKeyPath)
    {
        var current = sshKeyPath.Trim();
        if (current.Length > 0)
        {
            var dir = Path.GetDirectoryName(ExpandHome(current));
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }

        var ssh = Path.Combine(Home, ".ssh");
        return Directory.Exists(ssh) ? ssh : null;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ExpandHome(string path)
    {
        if (path == "~") return Home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Home, path[2..]);
        return path;
    }
}
