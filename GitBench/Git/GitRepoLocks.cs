using System.Collections.Concurrent;

namespace GitBench.Git;

/// <summary>
/// A shared resource a mutating git op serializes on. Two, because they are not the same resource
/// and are not even scoped the same way — see <see cref="GitRepoLocks"/>.
/// </summary>
internal enum GitResource
{
    // The index, the working tree and refs. Scoped to one working tree.
    LocalState,
    // Network traffic and remote-tracking refs. Scoped to one repository, worktrees included.
    Remote,
}

/// <summary>
/// The mutation locks of every repo the process has touched, one semaphore per
/// (resource, resource key) pair.
///
/// <para><b>LocalState</b> exists because every git write touches <c>.git/index.lock</c>: two writes
/// against the same repo at once (a checkout from the sidebar racing a stage from the local-changes
/// panel, or an impatient user double-clicking branches) collide with "Unable to create
/// '.git/index.lock': File exists". Serializing them means call sites cannot race each other, and
/// their own UI-busy flags are cosmetic rather than correctness guards. Keyed by working-tree path,
/// because that is what an index belongs to.</para>
///
/// <para><b>Remote</b> exists so <c>fetch --all --prune --recurse-submodules</c> stops holding the
/// semaphore a stage click needs. Push and fetch never touch the index; making them wait on disk
/// work bought nothing but a stalled panel — and, since the wait is blocking, a thread-pool thread
/// parked for a network round-trip. Keyed by the common git dir, so a primary and its linked
/// worktrees (one <c>.git</c>, one config, one set of remotes) share it.</para>
///
/// <para>An op that touches both resources takes LocalState first. Pull is the only such op, which
/// is what makes the ordering unable to invert; a pull nests further, into a submodule's LocalState,
/// and nothing takes a child's lock before its parent's.</para>
///
/// <para>Reads take no lock at all — libgit2 and the git CLI tolerate concurrent reads, and the next
/// refresh corrects any brief inconsistency.</para>
/// </summary>
internal sealed class GitRepoLocks
{
    private readonly Func<string, string?> _resolveCommonGitDir;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _localState = NewMap<SemaphoreSlim>();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _remote = NewMap<SemaphoreSlim>();
    // Resolving the common git dir spawns a git process, so each working tree pays for it once.
    private readonly ConcurrentDictionary<string, string> _commonGitDirs = NewMap<string>();

    /// <param name="resolveCommonGitDir">
    /// Resolves a working-tree path to the git dir its family shares (<c>git rev-parse
    /// --git-common-dir</c>). Returning null means "couldn't tell", which degrades the remote lock to
    /// per-working-tree rather than failing the op.
    /// </param>
    public GitRepoLocks(Func<string, string?> resolveCommonGitDir)
        => _resolveCommonGitDir = resolveCommonGitDir;

    /// <summary>
    /// The resource key <paramref name="repoPath"/> shares its <paramref name="resource"/> lock with.
    /// Two paths with the same key contend; two paths with different keys never do.
    /// </summary>
    public string KeyFor(GitResource resource, string repoPath)
        => resource == GitResource.Remote ? CommonGitDirKey(repoPath) : Normalize(repoPath);

    /// <summary>
    /// Takes the lock and releases it when the returned scope is disposed. Use with <c>using</c> so
    /// every mutation path serializes without a hand-written try/finally.
    /// </summary>
    public Scope Acquire(GitResource resource, string repoPath)
    {
        var map = resource == GitResource.Remote ? _remote : _localState;
        var sem = map.GetOrAdd(KeyFor(resource, repoPath), _ => new SemaphoreSlim(1, 1));
        sem.Wait();
        return new Scope(sem);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        internal Scope(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() => _sem.Release();
    }

    private string CommonGitDirKey(string repoPath)
    {
        var self = Normalize(repoPath);
        if (_commonGitDirs.TryGetValue(self, out var cached)) return cached;

        var resolved = self;
        try
        {
            if (_resolveCommonGitDir(repoPath) is { Length: > 0 } dir)
                resolved = Normalize(Path.IsPathRooted(dir) ? dir : Path.Combine(repoPath, dir));
        }
        catch
        {
            // Keep the fallback: a repo we can't interrogate still needs a usable lock.
        }
        _commonGitDirs[self] = resolved;
        return resolved;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }

    private static ConcurrentDictionary<string, TValue> NewMap<TValue>()
        => new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
