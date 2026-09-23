using GitBench.Infrastructure;

namespace GitBench.Features.Pairing;

/// <summary>A repository's working tree for the loop: snapshots through git, and the empty files
/// new-file stops start from. The file work happens off the UI thread.</summary>
internal sealed class GitPairingWorkspace : IPairingWorkspace
{
    private readonly string _repoPath;
    private readonly WorkingTreeSnapshots _snapshots;

    public GitPairingWorkspace(string repoPath, WorkingTreeSnapshots snapshots)
    {
        _repoPath = repoPath;
        _snapshots = snapshots;
    }

    public Task<SnapshotResult<TreeSnapshot>> CaptureAsync(CancellationToken ct) =>
        Task.Run(() => _snapshots.Capture(_repoPath), ct);

    public Task<SnapshotResult<string>> DiffAsync(TreeSnapshot from, TreeSnapshot to, CancellationToken ct) =>
        Task.Run(() => _snapshots.Diff(_repoPath, from, to), ct);

    public Task<FileCreation> EnsureEmptyFileAsync(string relativePath, CancellationToken ct) => Task.Run<FileCreation>(() =>
    {
        if (Absolute(relativePath) is not { } path) return new FileCreation.Refused($"{relativePath} is outside the repository.");
        try
        {
            if (File.Exists(path))
                return IsEmpty(path)
                    ? new FileCreation.AlreadyEmpty()
                    : new FileCreation.Refused($"{relativePath} has content now. Name a declaration in it.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            new FileStream(path, FileMode.CreateNew, FileAccess.Write).Dispose();
            return new FileCreation.Created();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new FileCreation.Refused($"{relativePath} could not be created: {e.Message}");
        }
    }, ct);

    public Task RemoveIfEmptyAsync(string relativePath, CancellationToken ct) => Task.Run(() =>
    {
        if (Absolute(relativePath) is not { } path) return;
        try
        {
            if (File.Exists(path) && IsEmpty(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }, ct);

    private static bool IsEmpty(string path) => File.ReadAllText(path).Trim().Length == 0;

    private string? Absolute(string relative)
    {
        var root = PathKey.Normalize(_repoPath);
        var full = PathKey.Normalize(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full
            : null;
    }
}
