namespace GitGui;

internal enum RemoteUrlScheme
{
    Ssh,
    Https,
    Other,
}

/// <summary>
/// Parses and rewrites git remote URLs between the two common transports the Edit Remote
/// dialog's scheme dropdown switches over: HTTPS (https://host/path) and SSH — both the
/// scp-like shorthand (git@host:path) and the explicit ssh:// form. Anything we can't
/// parse into host + path is reported as <see cref="RemoteUrlScheme.Other"/> and left
/// untouched by <see cref="Convert"/>, so exotic URLs round-trip unchanged.
/// </summary>
internal static class RemoteUrl
{
    public static RemoteUrlScheme Detect(string url)
    {
        var u = url.Trim();
        if (u.Length == 0) return RemoteUrlScheme.Other;
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return RemoteUrlScheme.Https;
        if (u.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return RemoteUrlScheme.Ssh;
        if (TryParseScpLike(u, out _, out _, out _)) return RemoteUrlScheme.Ssh;
        return RemoteUrlScheme.Other;
    }

    public static string Convert(string url, RemoteUrlScheme target)
    {
        var trimmed = url.Trim();
        if (!TryParse(trimmed, out var host, out var path, out var user))
            return url;

        switch (target)
        {
            case RemoteUrlScheme.Https:
                return $"https://{host}/{path}";
            case RemoteUrlScheme.Ssh:
                // The user component is only meaningful for SSH. An HTTPS source carries an
                // HTTP auth username (e.g. "Zee@github.com") that must not leak into the
                // SSH URL, so only preserve a user parsed from an SSH source; otherwise
                // default to the conventional "git".
                var sshUser = user.Length > 0 && Detect(trimmed) == RemoteUrlScheme.Ssh ? user : "git";
                return $"{sshUser}@{host}:{path}";
            default:
                return url;
        }
    }

    private static bool TryParse(string url, out string host, out string path, out string user)
    {
        host = string.Empty;
        path = string.Empty;
        user = string.Empty;
        if (url.Length == 0) return false;

        var schemeSep = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep >= 0)
        {
            var rest = url[(schemeSep + 3)..];

            var slash = rest.IndexOf('/');
            if (slash < 0) return false;

            var authority = rest[..slash];
            path = rest[(slash + 1)..];

            var at = authority.IndexOf('@');
            if (at >= 0)
            {
                user = authority[..at];
                authority = authority[(at + 1)..];
            }

            // Drop any :port from the authority.
            var colon = authority.IndexOf(':');
            host = colon >= 0 ? authority[..colon] : authority;
            return host.Length > 0 && path.Length > 0;
        }

        return TryParseScpLike(url, out host, out path, out user);
    }

    // scp-like shorthand: [user@]host:path — the colon separates host from path and must
    // come before any slash (otherwise the colon belongs to the path, e.g. a Windows path).
    private static bool TryParseScpLike(string url, out string host, out string path, out string user)
    {
        host = string.Empty;
        path = string.Empty;
        user = string.Empty;

        var colon = url.IndexOf(':');
        if (colon < 0) return false;
        var slash = url.IndexOf('/');
        if (slash >= 0 && slash < colon) return false;

        var authority = url[..colon];
        path = url[(colon + 1)..];

        var at = authority.IndexOf('@');
        if (at >= 0)
        {
            user = authority[..at];
            host = authority[(at + 1)..];
        }
        else
        {
            host = authority;
        }

        return host.Length > 0 && path.Length > 0;
    }
}
