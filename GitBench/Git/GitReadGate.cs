using System.Collections.Concurrent;
using System.Diagnostics;

namespace GitBench.Git;

/// <summary>
/// The kind of background read a permit is taken for. Times are attributed per (repo, kind) so a
/// consumer can ask for one kind's cost specifically — §7's adaptive debounce reads the <see
/// cref="GitReadKind.Status"/> duration rather than whatever read happened to land last.
/// </summary>
internal enum GitReadKind
{
    Status,
    // The refs-only ahead/behind read. Deliberately distinct from Status: it never walks the
    // working tree, so timing it as one would tell §7's adaptive debounce a status read is far
    // cheaper than it is.
    Sync,
    Commits,
    Branches,
    Discovery,
}

/// <summary>
/// The single throttle for background git <em>reads</em>. Bounds how many run at once across all
/// repos so a many-repo tree can't seek-thrash one spindle, and times each read per (repo, kind) so
/// §7's adaptive debounce can scale a repo's watcher debounce toward its own status-read cost.
///
/// <para>Reads only: mutations and network ops serialize on <see cref="GitRepoLocks"/> and never
/// enter here — the two mechanisms are disjoint by construction (a gated read takes no mutation lock;
/// a mutation never calls the gate), so they cannot deadlock.</para>
/// </summary>
internal interface IGitReadGate
{
    /// <summary>
    /// Waits until fewer than <see cref="GitReadGate.MaxConcurrentReads"/> reads are in flight, then
    /// returns a permit. Dispose it the instant the git read returns — it records the read's duration
    /// against (repoId, kind) and frees the slot. Dispose BEFORE marshalling results to the UI, so a
    /// permit is never held across the post.
    /// </summary>
    Task<Permit> Acquire(Guid repoId, GitReadKind kind);

    /// <summary>
    /// The most recent <see cref="GitReadKind.Status"/> read's wall-clock for <paramref name="repoId"/>,
    /// or null if none has completed. §7's seam: it polls this when it arms a debounce, so a reactive
    /// observable is unnecessary.
    /// </summary>
    TimeSpan? LastStatusReadDuration(Guid repoId);

    /// <summary>
    /// Whether any background read for <paramref name="repoId"/> is outstanding — holding a permit or
    /// still waiting for one. Because every background read passes through here, this is the one place
    /// that can answer "is this repo loading" for all of them at once; the RepoBar row spinner is
    /// projected from it. Thread-safe, and polled rather than observable: the answer changes on
    /// background threads, and its only consumer samples it on the UI thread's frame tick.
    /// </summary>
    bool HasOutstandingReads(Guid repoId);

    /// <summary>
    /// A held read slot. Disposing it times the read and releases the slot; do it inside the read
    /// block, before the result is posted to the UI.
    /// </summary>
    readonly struct Permit : IDisposable
    {
        private readonly Action? _release;
        internal Permit(Action release) => _release = release;
        public void Dispose() => _release?.Invoke();
    }
}

/// <summary>
/// The one gate the background read dispatchers (<see cref="Features.Repos.RepoStatusStore"/>, <see
/// cref="Features.Repos.RepoSnapshotStore"/>, and the sweep services via <see
/// cref="Features.Repos.StartupSweepCoordinator"/>) share. A single <see cref="SemaphoreSlim"/>
/// caps concurrent reads at <see cref="MaxConcurrentReads"/> — the active repo's own switch fan-out
/// (commits + branches + local) — so that fan-out runs unimpeded while cross-repo background reads
/// queue behind it instead of summing into a disk-thrashing burst. A DI singleton, like §11's <see
/// cref="GitRepoLocks"/>, and disposes its semaphore.
/// </summary>
internal sealed class GitReadGate : IGitReadGate, IDisposable
{
    // Exactly the active repo's switch fan-out (commits + branches + local). Larger admits cross-repo
    // thrash; smaller makes the active repo wait on itself. Fixed and low, like GitRepoLocks' size-1
    // per-repo semaphores — not disk-adaptive, not a preference.
    internal const int MaxConcurrentReads = 3;

    private readonly SemaphoreSlim _slots = new(MaxConcurrentReads, MaxConcurrentReads);
    private readonly ConcurrentDictionary<Guid, TimeSpan> _lastStatusRead = new();
    private readonly ConcurrentDictionary<Guid, int> _outstanding = new();

    public async Task<IGitReadGate.Permit> Acquire(Guid repoId, GitReadKind kind)
    {
        // Counted from when the read is asked for, not from when it is admitted. A read parked on a
        // full gate is one the user is waiting on — going by admission would blank the row's spinner
        // for exactly the queue wait this gate exists to create.
        _outstanding.AddOrUpdate(repoId, 1, static (_, n) => n + 1);
        try
        {
            await _slots.WaitAsync().ConfigureAwait(false);
        }
        catch
        {
            // Disposal faults parked waiters; the read never runs, so it is no longer outstanding.
            EndRead(repoId);
            throw;
        }

        var start = Stopwatch.GetTimestamp();
        return new IGitReadGate.Permit(() =>
        {
            if (kind == GitReadKind.Status)
                _lastStatusRead[repoId] = Stopwatch.GetElapsedTime(start);
            EndRead(repoId);
            _slots.Release();
        });
    }

    public TimeSpan? LastStatusReadDuration(Guid repoId)
        => _lastStatusRead.TryGetValue(repoId, out var duration) ? duration : null;

    public bool HasOutstandingReads(Guid repoId)
        => _outstanding.TryGetValue(repoId, out var count) && count > 0;

    private void EndRead(Guid repoId)
        => _outstanding.AddOrUpdate(repoId, 0, static (_, n) => n > 0 ? n - 1 : 0);

    public void Dispose() => _slots.Dispose();
}
