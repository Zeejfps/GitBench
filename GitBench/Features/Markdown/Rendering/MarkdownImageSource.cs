namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Where a markdown surface's relative image paths come from: the commit a diff shows, the
/// working tree a file browser lists. Provided into the build context by the surface (via
/// <c>Provide&lt;IMarkdownImageSource&gt;</c>); a document rendered without one (an assistant
/// reply, a hover card) can only show remote images. Implementations are records so an unchanged
/// surface state compares equal and does not rebuild the document.
/// </summary>
internal interface IMarkdownImageSource
{
    /// <summary>The '/'-separated directory of the markdown file, relative to the root the paths
    /// resolve against; empty at the root.</summary>
    string BaseDir { get; }

    /// <summary>The bytes of the file at <paramref name="path"/> — '/'-separated, relative to the
    /// root, already resolved and confined by <see cref="MarkdownImagePath.Resolve"/> — or null
    /// when it is missing, unreadable, or over <paramref name="maxBytes"/>. Called off the UI
    /// thread.</summary>
    byte[]? Read(string path, int maxBytes);
}

/// <summary>
/// Resolves an image's <c>src</c> against the document it sits in, the way a repository host
/// does: relative to the file's directory, a leading '/' meaning the root, never above the root.
/// </summary>
internal static class MarkdownImagePath
{
    private static readonly char[] QueryOrFragment = { '?', '#' };

    /// <summary>True when <paramref name="src"/> is an absolute http(s) URL — fetched, not read.</summary>
    public static bool IsRemote(string src, out Uri uri)
    {
        if (Uri.TryCreate(src, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    /// <summary>
    /// The normalized '/'-separated root-relative path <paramref name="src"/> names from
    /// <paramref name="baseDir"/>, or null when it cannot be a file under the root: empty, carries
    /// a scheme or drive letter, or climbs past the root with "..". A query or fragment is dropped
    /// and percent-escapes decoded, so "docs/my%20shot.png?raw=1" reads the file it points at.
    /// </summary>
    public static string? Resolve(string baseDir, string src)
    {
        var cut = src.IndexOfAny(QueryOrFragment);
        if (cut >= 0) src = src[..cut];
        src = Uri.UnescapeDataString(src).Replace('\\', '/');
        if (src.Length == 0 || src.Contains(':')) return null;

        var rooted = src[0] == '/';
        var combined = rooted ? src : baseDir.Length == 0 ? src : baseDir + "/" + src;
        var segments = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    /// <summary>The '/'-separated directory part of a root-relative file path; empty at the root.</summary>
    public static string DirectoryOf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }
}
