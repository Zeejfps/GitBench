using GitBench.Git;

namespace GitBench.Features.Assistant.Tools;

/// <summary>What asking for a path settled: the file to address, or the sentence the model is told
/// instead. Either the pair of paths is set or the refusal is.</summary>
internal readonly record struct RepoFileResolution(string? FullPath, string? RelativePath, string? Refusal)
{
    public static RepoFileResolution Allowed(string fullPath, string relativePath) =>
        new(fullPath, relativePath, null);

    public static RepoFileResolution Refused(string reason) => new(null, null, reason);
}

/// <summary>
/// Decides whether the assistant may address a file of one repository, and refuses in the model's
/// own language when it may not.
/// </summary>
/// <remarks>
/// The rest of the toolset reaches git data for one repository and nothing else; a tool that takes
/// a path is the one place that property can be lost, and it is lost silently because reads need no
/// approval. So the answer is "yes" only when every one of these holds: the path stays inside the
/// repository after normalisation <em>and</em> after every symlink on the way is followed, its name
/// is not one of the credential-shaped ones, and the ignore rules do not match it.
/// <see cref="Resolve"/> adds that git tracks the file and that it is on disk, which is what opening
/// a file needs; <see cref="ResolveForDiff"/> drops those two, because a diff legitimately covers a
/// file that was deleted or that exists only at an older commit. The name and ignore rules are the
/// ones that carry the weight there — an untracked <c>.env</c> or <c>id_rsa</c> is exactly what a
/// diff against the null device would otherwise render in full.
/// </remarks>
internal static class RepoFileGuard
{
    // Names that are a secret often enough that being tracked is not a reason to hand one over —
    // a repository that has committed its .env has made a mistake, not granted permission.
    private static readonly string[] DeniedNames =
    [
        ".env", ".envrc", ".netrc", "_netrc", ".npmrc", ".pgpass", ".htpasswd",
        ".git-credentials", "credentials", "credentials.json", "secrets.json", "secrets.yaml",
        "secrets.yml", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
    ];

    private static readonly string[] DeniedExtensions =
    [
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".ppk", ".asc", ".gpg", ".kdbx",
    ];

    private static readonly string[] DeniedDirectories = [".ssh", ".aws", ".gnupg", ".gpg"];

    /// <summary>Settles a path a tool means to open in the working tree.</summary>
    public static RepoFileResolution Resolve(IGitRepositoryReader git, Repo repo, string? requested) =>
        Resolve(git, repo, requested, requireInWorkingTree: true);

    /// <summary>Settles a path a tool means to diff, at any side or commit.</summary>
    public static RepoFileResolution ResolveForDiff(IGitRepositoryReader git, Repo repo, string? requested) =>
        Resolve(git, repo, requested, requireInWorkingTree: false);

    private static RepoFileResolution Resolve(
        IGitRepositoryReader git, Repo repo, string? requested, bool requireInWorkingTree)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return RepoFileResolution.Refused("Argument 'path' is required.");

        var raw = requested.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(requested) || Path.IsPathRooted(raw) || raw.StartsWith("//", StringComparison.Ordinal))
            return Outside(requested);

        var segments = new List<string>();
        foreach (var segment in raw.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") return Outside(requested);
            segments.Add(segment);
        }

        if (segments.Count == 0)
            return RepoFileResolution.Refused($"'{requested}' names no file.");

        var relative = string.Join('/', segments);
        if (IsDenied(segments))
            return RepoFileResolution.Refused(
                $"'{relative}' looks like a credentials file, so it is off limits regardless of "
                + "whether the repository tracks it.");

        if (!TryWalkInsideRepo(repo.Path, segments, out var fullPath))
            return Outside(requested);

        if (git.IsPathIgnored(repo, relative))
            return RepoFileResolution.Refused($"'{relative}' is ignored by this repository's ignore rules.");

        if (requireInWorkingTree)
        {
            if (!git.IsPathTracked(repo, relative))
                return RepoFileResolution.Refused(
                    $"Git does not track '{relative}'.{Nearby(git, repo, relative)} This tool reads "
                    + "tracked files only — use get_local_changes or get_diff to see what is in the "
                    + "working tree.");

            if (!File.Exists(fullPath))
                return RepoFileResolution.Refused($"'{relative}' is tracked but not present in the working tree.");
        }

        return RepoFileResolution.Allowed(fullPath, relative);
    }

    /// <summary>The tracked paths a search may return: everything but the credential-shaped ones,
    /// which stay invisible whether they are asked for or stumbled upon.</summary>
    public static IReadOnlyList<string> Searchable(IReadOnlyList<string> trackedPaths)
    {
        var visible = new List<string>(trackedPaths.Count);
        foreach (var path in trackedPaths)
        {
            var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0 && !IsDenied(segments)) visible.Add(path);
        }

        return visible;
    }

    // A wrong path is nearly always a near-miss of a real one — the wrong directory, a swapped pair
    // of letters — so the refusal carries the candidates rather than making the model guess again.
    // Only paid for on the failure path: one `ls-files` after the read has already been refused.
    private static string Nearby(IGitRepositoryReader git, Repo repo, string relative)
    {
        try
        {
            var matches = PathSearch.Rank(Searchable(git.ListTrackedFiles(repo)), relative, 3)
                .Where(match => match.Score >= PathSearch.SuggestionFloor)
                .Select(match => $"'{match.Path}'")
                .ToArray();
            return matches.Length == 0 ? string.Empty : " Did you mean " + string.Join(", ", matches) + "?";
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static RepoFileResolution Outside(string requested) =>
        RepoFileResolution.Refused(
            $"'{requested}' resolves outside the repository. Paths must be repo-relative, and "
            + "'..', absolute paths and links that leave the checkout are refused.");

    private static bool IsDenied(IReadOnlyList<string> segments)
    {
        for (var i = 0; i < segments.Count - 1; i++)
            if (DeniedDirectories.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
                return true;

        var name = segments[^1];
        if (DeniedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) return true;
        return DeniedExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    // Descends one segment at a time, following any link it meets and re-checking containment after
    // each step. A single GetFullPath would miss all of this: it is pure string arithmetic and never
    // touches the filesystem, so a directory symlink partway down the path escapes it unnoticed.
    private static bool TryWalkInsideRepo(string repoPath, IReadOnlyList<string> segments, out string fullPath)
    {
        fullPath = string.Empty;
        string root;
        try
        {
            root = Canonical(Path.GetFullPath(repoPath));
        }
        catch (Exception)
        {
            return false;
        }

        var current = root;
        foreach (var segment in segments)
        {
            try
            {
                current = Canonical(Path.GetFullPath(Path.Combine(current, segment)));
            }
            catch (Exception)
            {
                return false;
            }

            if (!IsInside(root, current)) return false;
        }

        fullPath = current;
        return true;
    }

    private static string Canonical(string path)
    {
        var isDirectory = Directory.Exists(path);
        // Nothing there to follow, and nothing there can be a link — a diff of a deleted file walks
        // segments that no longer exist on disk.
        if (!isDirectory && !File.Exists(path)) return path;

        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? path : Path.GetFullPath(target.FullName);
    }

    private static bool IsInside(string root, string candidate)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(root, candidate, comparison)) return true;

        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }
}
