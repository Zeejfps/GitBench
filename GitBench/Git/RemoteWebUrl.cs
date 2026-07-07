namespace GitBench.Git;

/// <summary>
/// Converts a git remote URL into the https address a browser can open — e.g.
/// <c>git@github.com:user/repo.git</c> becomes <c>https://github.com/user/repo</c>. Handles the
/// four clone-URL shapes (http(s), ssh://, git://, scp-like) and returns null for remotes with no
/// web equivalent (local paths, file://).
/// </summary>
public static class RemoteWebUrl
{
    public static string? FromRemoteUrl(string remoteUrl)
    {
        var url = remoteUrl.Trim();
        if (url.Length == 0) return null;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var http) || http.Host.Length == 0) return null;
            // Authority keeps host:port but drops any embedded credentials.
            return $"{http.Scheme}://{http.Authority}{TrimRepoPath(http.AbsolutePath)}";
        }

        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("git://", StringComparison.OrdinalIgnoreCase))
        {
            // The ssh user and port have no web counterpart, so only the host survives.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var ssh) || ssh.Host.Length == 0) return null;
            return $"https://{ssh.Host}{TrimRepoPath(ssh.AbsolutePath)}";
        }

        return FromScpLike(url);
    }

    // The scheme-less [user@]host:path form (`git@github.com:user/repo.git`). Requiring either a
    // user part or a dotted host rejects the look-alikes: Windows drive paths (C:/x) and plain
    // filesystem paths.
    private static string? FromScpLike(string url)
    {
        if (url.Contains('\\')) return null;
        var at = url.IndexOf('@');
        var colon = url.IndexOf(':', at + 1);
        if (colon <= at + 1) return null;

        var host = url[(at + 1)..colon];
        if (host.Length == 0 || host.Contains('/')) return null;
        if (at < 0 && !host.Contains('.')) return null;

        var path = url[(colon + 1)..].TrimStart('/');
        if (path.Length == 0) return null;
        return $"https://{host}{TrimRepoPath("/" + path)}";
    }

    private static string TrimRepoPath(string path)
    {
        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        return path.TrimEnd('/');
    }
}
