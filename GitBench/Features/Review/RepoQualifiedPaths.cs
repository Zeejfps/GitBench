namespace GitBench.Features.Review;

/// <summary>
/// The repo-qualified path scheme for the cross-repo review surface (Locked decision #4): a file's
/// identity on that surface is <c>"&lt;repoKey&gt;/&lt;repo-relative path&gt;"</c>, with the repo's
/// (de-duplicated) display name as the tree's top-level folder. The scheme is pure and reversible:
/// <see cref="Qualify"/> prefixes a member's key, <see cref="TryResolve"/> splits it back off. Git-facing
/// calls always receive the unqualified path plus the member's <c>Repo</c>; only the aggregating view
/// models ever see the qualified form, so the shared tree/diff widgets stay repo-blind.
/// </summary>
internal static class RepoQualifiedPaths
{
    public const char Separator = '/';

    /// <summary>
    /// A stable, unique, slash-free key per member, in the given order — the display name, with any
    /// separators flattened, and duplicate names disambiguated by a <c>" (2)"</c>, <c>" (3)"</c> …
    /// suffix so two repos sharing a display name still resolve unambiguously.
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> BuildKeys(
        IReadOnlyList<(Guid RepoId, string DisplayName)> members)
    {
        var keys = new Dictionary<Guid, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (repoId, displayName) in members)
        {
            if (keys.ContainsKey(repoId)) continue;
            var baseKey = Sanitize(displayName);
            var key = baseKey;
            var n = 2;
            while (!used.Add(key)) key = $"{baseKey} ({n++})";
            keys[repoId] = key;
        }
        return keys;
    }

    /// <summary>Prefixes <paramref name="path"/> with the member's <paramref name="repoKey"/>.</summary>
    public static string Qualify(string repoKey, string path) => $"{repoKey}{Separator}{path}";

    /// <summary>
    /// Splits a qualified path back into its member value (looked up in <paramref name="byKey"/> by the
    /// leading key segment) and its unqualified repo-relative path. False — with <paramref name="path"/>
    /// echoed back unchanged — when the leading segment is not a known key (e.g. a bare single-repo path).
    /// </summary>
    public static bool TryResolve<T>(
        string qualified, IReadOnlyDictionary<string, T> byKey, out T value, out string path)
    {
        var idx = qualified.IndexOf(Separator);
        if (idx > 0)
        {
            var key = qualified[..idx];
            if (byKey.TryGetValue(key, out value!))
            {
                path = qualified[(idx + 1)..];
                return true;
            }
        }
        value = default!;
        path = qualified;
        return false;
    }

    private static string Sanitize(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "repo" : name.Trim();
        return trimmed.Replace(Separator, '-');
    }
}
