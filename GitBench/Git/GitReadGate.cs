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
/// How a read ranks for admission. Derived from (repo, kind) at the moment a permit frees rather
/// than passed in by the caller, so switching repos re-ranks reads that are already queued.
/// </summary>
internal enum GitReadPriority
{
    // The repo the user is looking at. Admitted ahead of everything else, and never blocked by a
    // saturated gate: one permit is held back for this class alone.
    Foreground,
    // Another repo's status/commits/branches — a watcher tick or a background refresh.
    Background,
    // Worktree and submodule discovery. Sweep work even on the active repo: nobody is waiting on it.
    Sweep,
}

/// <summary>
/// The single throttle for background git <em>reads</em>. Bounds how many run at once across all
/// repos so a many-repo tree can't seek-thrash one spindle, admits the repo the user is looking at
/// ahead of every other repo's, and times each read per (repo, kind) so §7's adaptive debounce can
/// scale a repo's watcher debounce toward its own status-read cost.
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
    /// The repo the user is looking at, or null for none. Its reads are admitted ahead of every other
    /// repo's and keep a permit reserved, so they never queue behind a sweep. Reads already waiting
    /// are re-ranked by this, not stuck with the class they had when they queued. UI thread only.
    /// </summary>
    void SetForegroundRepo(Guid? repoId);

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
/// cref="Features.Repos.StartupSweepCoordinator"/>) share. It caps concurrent reads at <see
/// cref="MaxConcurrentReads"/> — the active repo's own switch fan-out (commits + branches + local) —
/// so cross-repo reads queue instead of summing into a disk-thrashing burst, and it orders that queue
/// by <see cref="GitReadPriority"/> so the repo on screen is served first. A DI singleton, like §11's
/// <see cref="GitRepoLocks"/>.
/// </summary>
internal sealed class GitReadGate : IGitReadGate, IDisposable
{
    // Exactly the active repo's switch fan-out (commits + branches + local). Larger admits cross-repo
    // thrash; smaller makes the active repo wait on itself. Fixed and low, like GitRepoLocks' size-1
    // per-repo semaphores — not disk-adaptive, not a preference.
    internal const int MaxConcurrentReads = 3;

    // One permit no Background or Sweep read may take, so a read the user just caused is admitted
    // without waiting out a cold status walk that is already running. The trade is background sweeps
    // draining two-wide instead of three — foreground latency bought with background throughput,
    // the same trade StartupSweepCoordinator already makes by deferring those sweeps.
    private const int ForegroundReserve = 1;

    private readonly object _lock = new();
    private readonly List<Waiter> _waiters = new();
    private readonly ConcurrentDictionary<Guid, TimeSpan> _lastStatusRead = new();
    private readonly ConcurrentDictionary<Guid, int> _outstanding = new();
    private int _inFlight;
    private int _sharedInFlight;
    private Guid? _foregroundRepo;
    private bool _disposed;

    public Task<IGitReadGate.Permit> Acquire(Guid repoId, GitReadKind kind)
    {
        // Counted from when the read is asked for, not from when it is admitted. A read parked on a
        // full gate is one the user is waiting on — going by admission would blank the row's spinner
        // for exactly the queue wait this gate exists to create.
        _outstanding.AddOrUpdate(repoId, 1, static (_, n) => n + 1);
        lock (_lock)
        {
            if (_disposed)
            {
                EndRead(repoId);
                return Task.FromException<IGitReadGate.Permit>(new ObjectDisposedException(nameof(GitReadGate)));
            }

            var priority = Classify(repoId, kind);
            if (CanAdmit(priority)) return Task.FromResult(Admit(repoId, kind, priority));

            var waiter = new Waiter(repoId, kind);
            _waiters.Add(waiter);
            return waiter.Permit.Task;
        }
    }

    public void SetForegroundRepo(Guid? repoId)
    {
        lock (_lock)
        {
            if (_foregroundRepo == repoId) return;
            _foregroundRepo = repoId;
            // A parked read for the newly active repo is foreground as of now, and the permit held
            // back for that class may be free — so a switch can admit without any read finishing.
            Pump();
        }
    }

    public TimeSpan? LastStatusReadDuration(Guid repoId)
        => _lastStatusRead.TryGetValue(repoId, out var duration) ? duration : null;

    public bool HasOutstandingReads(Guid repoId)
        => _outstanding.TryGetValue(repoId, out var count) && count > 0;

    public void Dispose()
    {
        List<Waiter> parked;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            parked = new List<Waiter>(_waiters);
            _waiters.Clear();
        }

        // Parked waiters are completed rather than faulted: their read then unwinds through the same
        // permit-release path it takes in normal operation — which is where RepoStatusStore frees its
        // per-repo coalescing slot — instead of through an exception on shutdown. The permit is inert;
        // there is no longer a gate to release it to.
        foreach (var waiter in parked)
        {
            EndRead(waiter.RepoId);
            waiter.Permit.TrySetResult(default);
        }
    }

    private GitReadPriority Classify(Guid repoId, GitReadKind kind)
        => kind == GitReadKind.Discovery ? GitReadPriority.Sweep
            : repoId == _foregroundRepo ? GitReadPriority.Foreground
            : GitReadPriority.Background;

    private bool CanAdmit(GitReadPriority priority)
    {
        if (_inFlight >= MaxConcurrentReads) return false;
        if (priority == GitReadPriority.Foreground) return true;
        // With no repo on screen nothing can ever claim the reserved permit, so holding it back would
        // idle a slot for a class that cannot arrive.
        var reserved = _foregroundRepo is null ? 0 : ForegroundReserve;
        return _sharedInFlight < MaxConcurrentReads - reserved;
    }

    private IGitReadGate.Permit Admit(Guid repoId, GitReadKind kind, GitReadPriority priority)
    {
        _inFlight++;
        if (priority != GitReadPriority.Foreground) _sharedInFlight++;

        var start = Stopwatch.GetTimestamp();
        var released = false;
        return new IGitReadGate.Permit(() =>
        {
            lock (_lock)
            {
                if (released) return;
                released = true;
                if (kind == GitReadKind.Status)
                    _lastStatusRead[repoId] = Stopwatch.GetElapsedTime(start);
                EndRead(repoId);
                _inFlight--;
                if (priority != GitReadPriority.Foreground) _sharedInFlight--;
                Pump();
            }
        });
    }

    // Hands the free permits to the best waiters that can take them. Called under the lock; waiters
    // complete asynchronously (see Waiter), so nothing re-enters the gate from here.
    private void Pump()
    {
        while (true)
        {
            var next = NextAdmissible();
            if (next < 0) return;
            var waiter = _waiters[next];
            _waiters.RemoveAt(next);
            waiter.Permit.SetResult(Admit(waiter.RepoId, waiter.Kind, Classify(waiter.RepoId, waiter.Kind)));
        }
    }

    // The best-ranked waiter that can be admitted right now, classified as of this moment rather than
    // as of when it queued. The list is append-ordered, so scanning it forward and keeping only a
    // strictly better rank leaves admission FIFO within a class.
    private int NextAdmissible()
    {
        var best = -1;
        var bestRank = int.MaxValue;
        for (var i = 0; i < _waiters.Count; i++)
        {
            var priority = Classify(_waiters[i].RepoId, _waiters[i].Kind);
            if ((int)priority >= bestRank || !CanAdmit(priority)) continue;
            best = i;
            bestRank = (int)priority;
            if (priority == GitReadPriority.Foreground) break;
        }
        return best;
    }

    private void EndRead(Guid repoId)
        => _outstanding.AddOrUpdate(repoId, 0, static (_, n) => n > 0 ? n - 1 : 0);

    private sealed class Waiter
    {
        internal readonly Guid RepoId;
        internal readonly GitReadKind Kind;

        // Asynchronous continuations so completing a waiter inside the gate's lock can't run the
        // read's own continuation — and whatever it calls back into — on the releasing thread.
        internal readonly TaskCompletionSource<IGitReadGate.Permit> Permit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Waiter(Guid repoId, GitReadKind kind)
        {
            RepoId = repoId;
            Kind = kind;
        }
    }
}
