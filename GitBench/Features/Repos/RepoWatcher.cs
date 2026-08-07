using GitBench.Git;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Watches a single repository's filesystem for changes the user makes outside the
// app (editor saves, terminal `git` commands, builds, IDE checkouts, …) and turns
// them into the same MessageBus signals the in-app presenters already use.
//
// The set of roots watched depends on what a recursive watch costs — see RepoWatchRoots, which owns
// that decision. On Windows and macOS it is one recursive watcher rooted at the working tree, split
// by path at delivery:
//   * Working tree edits → WorkingTreeChangedMessage.
//   * gitdir paths → ClassifyGitChange → refs / worktrees / submodules.
// A second watcher rooted at the gitdir would be a pure duplicate there, since the tree watcher's
// subtree already covers it.
//
// On Linux it is a handful of narrow roots instead, because a recursive FileSystemWatcher costs one
// inotify instance plus one watch per directory in the subtree, both drawn from a per-user budget
// shared with every other application on the machine. See WatcherDiagnostics.
//
// Either way the gitdir is the *resolved* one: `<repo>/.git` is a gitlink file, not a directory, for
// linked worktrees and submodules. See RepoGitDir.
//
// FSW fires events on threadpool threads in storms (a single editor save can be 3-5
// events; a build or git checkout can be thousands), so we debounce per channel and
// post the final broadcast through IUiDispatcher onto the UI thread.
//
// Design note — feedback loop avoidance:
//   We intentionally do NOT call libgit2 inside the debounce callback (e.g. to hash
//   a status snapshot and suppress no-op broadcasts). libgit2's RetrieveStatus updates
//   `.git/index`'s stat cache as a side effect, which fires our own watcher, which
//   schedules another debounce, which calls libgit2 again — an infinite loop. We also do
//   NOT treat `.git/index` as a working-tree signal for the same reason: every read-side
//   status call by the VM would re-trigger our watcher.
//
//   The cost: saves to `.gitignored` paths produce a broadcast and a redundant VM
//   GetLocalChanges call, even though git's view didn't change. That's cheap because
//   LocalChangesViewModel keeps the panels mounted during refresh (see LocalChangesState's
//   derived Placeholder) and identical snapshots produce no visible repaint beyond the
//   row list re-bind.
internal sealed class RepoWatcher : IDisposable
{
    private const int DebounceFloorMs = 250;      // today's value; the coalescing window never drops below it
    private const int DebounceCeilingMs = 2000;   // a pathological read must not make the app feel dead
    private const double DecayAlpha = 0.25;       // how fast the window relaxes when reads speed up
    private const int FswBufferBytes = 64 * 1024;

    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Repo _repo;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly IRepoActivityTracker _activity;
    private readonly IGitReadGate _readGate;

    private readonly List<Attached> _watchers;
    private readonly Channel _workingTree;
    private readonly Channel _refs;
    private readonly Channel _worktrees;
    private readonly Channel _submodules;
    private readonly Channel[] _channels;

    private readonly string _gitDirPrefix;
    private readonly string _gitmodulesPath;

    // Guards every Timer.Change against concurrent disposal. FSW events fire on threadpool
    // threads, so a Schedule* call can race Dispose(); without this, Change() can land on an
    // already-disposed Timer and throw ObjectDisposedException on a pool thread (process crash).
    private readonly object _timerLock = new();
    private int _disposed;

    // Guarded by _timerLock, like every value the arm reads. One RepoWatcher per repo, so one EWMA.
    private double _smoothedReadMs;
    private TimeSpan? _lastSample;                 // last value seen from the gate, to fold each read once

    public RepoWatcher(Repo repo, IUiDispatcher dispatcher, IMessageBus bus, IRepoActivityTracker activity, IGitReadGate readGate)
    {
        _repo = repo;
        _dispatcher = dispatcher;
        _bus = bus;
        _activity = activity;
        _readGate = readGate;
        var gitDir = RepoGitDir.Resolve(repo.Path);
        _gitDirPrefix = gitDir + Path.DirectorySeparatorChar;
        _gitmodulesPath = Path.Combine(repo.Path, ".gitmodules");

        _workingTree = NewChannel(id => _bus.Broadcast(new WorkingTreeChangedMessage(id)));
        _refs = NewChannel(id => _bus.Broadcast(new RefsChangedMessage(id)));
        _worktrees = NewChannel(id => _bus.Broadcast(new WorktreesChangedMessage(id)));
        _submodules = NewChannel(id => _bus.Broadcast(new SubmodulesChangedMessage(id)));
        _channels = [_workingTree, _refs, _worktrees, _submodules];

        _watchers = Attach(RepoWatchRoots.For(repo.Path, gitDir, RepoWatchRoots.CurrentCost));
    }

    // A watcher plus the delegates it was subscribed with, so Dispose can detach exactly what it
    // attached — the handler pair differs per root kind.
    private sealed record Attached(
        FileSystemWatcher Watcher,
        FileSystemEventHandler OnEvent,
        RenamedEventHandler OnRenamed);

    private List<Attached> Attach(IReadOnlyList<WatchRoot> roots)
    {
        var attached = new List<Attached>(roots.Count);
        foreach (var root in roots)
        {
            if (!ShouldAttach(root)) continue;
            if (TryCreateWatcher(root) is { } watcher) attached.Add(watcher);
        }
        return attached;
    }

    // A root git creates on demand is skipped while absent. The `.gitmodules` root is skipped when
    // the file it exists for is absent, which is most repos: on Linux every root is its own inotify
    // instance out of a 128-per-user budget, so an unused one is a third of a repo's cost for
    // nothing. A `.gitmodules` appearing later is picked up by RepoReconcileService's focus gain,
    // which broadcasts the submodules channel.
    private bool ShouldAttach(WatchRoot root) => root.Kind switch
    {
        WatchRootKind.Gitmodules => File.Exists(_gitmodulesPath),
        _ => !root.Optional || Directory.Exists(root.Path),
    };

    // Null when the OS refuses the watch — a repo on a disconnected drive, an unreadable path, or
    // an exhausted Linux inotify budget. We just won't notice that class of change for this repo;
    // the user can still refresh by switching repos or performing an in-app op. Reported rather than
    // swallowed, because the budget case has consequences well outside this process.
    private Attached? TryCreateWatcher(WatchRoot root)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(root.Path)
            {
                IncludeSubdirectories = root.Recursive,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.CreationTime,
                InternalBufferSize = FswBufferBytes,
            };
            var onEvent = EventHandlerFor(root.Kind);
            var onRenamed = RenameHandlerFor(root.Kind);
            watcher.Created += onEvent;
            watcher.Changed += onEvent;
            watcher.Deleted += onEvent;
            watcher.Renamed += onRenamed;
            watcher.Error += OnError;
            // Enabled only once the handlers are attached, and separately from the initializer:
            // this is the call that allocates the inotify instance and walks the tree adding
            // watches, so it is the one that throws when the budget is gone.
            watcher.EnableRaisingEvents = true;
            WatcherDiagnostics.Created(root.Path);
            return new Attached(watcher, onEvent, onRenamed);
        }
        catch (Exception e)
        {
            watcher?.Dispose();
            WatcherDiagnostics.Failed(root.Path, e);
            return null;
        }
    }

    private FileSystemEventHandler EventHandlerFor(WatchRootKind kind) => kind switch
    {
        WatchRootKind.WorkingTree => OnTreeEvent,
        WatchRootKind.GitDir => OnGitDirEvent,
        _ => OnGitmodulesEvent,
    };

    private RenamedEventHandler RenameHandlerFor(WatchRootKind kind) => kind switch
    {
        WatchRootKind.WorkingTree => OnTreeRenamed,
        WatchRootKind.GitDir => OnGitDirRenamed,
        _ => OnGitmodulesRenamed,
    };

    // A gitdir root sees nothing else, so there is no working-tree half to split off here.
    private void OnGitDirEvent(object sender, FileSystemEventArgs e)
        => ClassifyGitChange(ToGitRelativePath(e.FullPath));

    private void OnGitDirRenamed(object sender, RenamedEventArgs e)
    {
        ClassifyGitChange(ToGitRelativePath(e.FullPath));
        ClassifyGitChange(ToGitRelativePath(e.OldFullPath));
    }

    // The non-recursive working-tree root deliberately acts on `.gitmodules` and nothing else; the
    // reasoning is in RepoWatchRoots.
    private void OnGitmodulesEvent(object sender, FileSystemEventArgs e)
    {
        if (IsGitmodules(e.FullPath)) Schedule(_submodules);
    }

    private void OnGitmodulesRenamed(object sender, RenamedEventArgs e)
    {
        if (IsGitmodules(e.FullPath) || IsGitmodules(e.OldFullPath)) Schedule(_submodules);
    }

    private void OnTreeEvent(object sender, FileSystemEventArgs e)
    {
        // ToGitRelativePath yields null for anything outside *this* repo's gitdir, so a nested
        // submodule's own `.git/` is dropped here exactly as it was when it fell to the bare
        // early-return: its churn belongs to that submodule's watcher, not the parent's.
        if (IsUnderGit(e.FullPath))
        {
            ClassifyGitChange(ToGitRelativePath(e.FullPath));
            return;
        }
        // .gitmodules lives in the working tree, but edits to it specifically should
        // re-run submodule discovery rather than just bumping the WorkingTree channel
        // (which would only re-read GetLocalChanges, not the submodule list).
        if (IsGitmodules(e.FullPath))
            Schedule(_submodules);
        Schedule(_workingTree);
    }

    private void OnTreeRenamed(object sender, RenamedEventArgs e)
    {
        if (IsGitmodules(e.FullPath) || IsGitmodules(e.OldFullPath))
            Schedule(_submodules);

        var newUnderGit = IsUnderGit(e.FullPath);
        var oldUnderGit = IsUnderGit(e.OldFullPath);
        if (newUnderGit) ClassifyGitChange(ToGitRelativePath(e.FullPath));
        if (oldUnderGit) ClassifyGitChange(ToGitRelativePath(e.OldFullPath));
        if (newUnderGit && oldUnderGit) return;
        Schedule(_workingTree);
    }

    internal void ClassifyGitChange(string? gitRelativePath)
    {
        if (gitRelativePath == null) return;

        // NOTE: `.git/index` is deliberately not mapped. libgit2's read-side status call
        // (called from LocalChangesViewModel on every working-tree event) updates the
        // index stat cache, which would fire this watcher and cause an infinite loop.
        // The cost is that external `git add`/`git reset` from a terminal won't be
        // auto-detected; the user can refresh by switching repos or by making any
        // working-tree change.

        if (string.Equals(gitRelativePath, "HEAD", StringComparison.Ordinal)
            || string.Equals(gitRelativePath, "packed-refs", StringComparison.Ordinal)
            || string.Equals(gitRelativePath, "FETCH_HEAD", StringComparison.Ordinal)
            || string.Equals(gitRelativePath, "ORIG_HEAD", StringComparison.Ordinal)
            || string.Equals(gitRelativePath, "MERGE_HEAD", StringComparison.Ordinal)
            || gitRelativePath.StartsWith("refs/", StringComparison.Ordinal))
        {
            Schedule(_refs);
            return;
        }

        // .git/worktrees/<name>/ carries two unrelated facts, and they go to two channels.
        // The directory itself appearing or vanishing means the SET changed (`git worktree
        // add`/`remove`). Everything *inside* one worktree's gitdir belongs to that worktree,
        // and needs the same per-file whitelist as modules/ below — a `git status` run in a
        // worktree rewrites its own `.git/worktrees/<name>/index` stat cache, which lands in
        // the primary's tree, so treating any change here as a set change drags a full
        // `git worktree list` rediscovery behind every tick in a worktree.
        if (gitRelativePath.Equals("worktrees", StringComparison.Ordinal))
        {
            Schedule(_worktrees);
            return;
        }
        if (gitRelativePath.StartsWith("worktrees/", StringComparison.Ordinal))
        {
            var afterWorktrees = gitRelativePath.Substring("worktrees/".Length);
            var nextSlash = afterWorktrees.IndexOf('/');
            if (nextSlash < 0)
            {
                Schedule(_worktrees);
                return;
            }
            // Worktrees share refs/heads with the primary, so the primary's Refs channel is the
            // correct carrier: WorktreeSyncService already fans RefsChangedMessage(primary) out
            // to every worktree child. The watcher has no registry and can't name the worktree.
            var perWorktree = afterWorktrees.Substring(nextSlash + 1);
            if (perWorktree.Equals("HEAD", StringComparison.Ordinal)
                || perWorktree.Equals("ORIG_HEAD", StringComparison.Ordinal)
                || perWorktree.Equals("MERGE_HEAD", StringComparison.Ordinal)
                || perWorktree.Equals("REBASE_HEAD", StringComparison.Ordinal)
                || perWorktree.StartsWith("refs/", StringComparison.Ordinal))
            {
                Schedule(_refs);
            }
            // index / index.lock / logs / gitdir / commondir / locked — ignored, exactly as
            // modules/<name>/index is.
            return;
        }

        // .git/modules/<name>/ holds each submodule's own gitdir. Same feedback-loop trap
        // as `.git/index`: read-only commands like `git submodule status` (called from
        // ListSubmodules during every LocalChanges load) write to each submodule's index
        // stat cache, which lives at .git/modules/<name>/index — broadcasting on those
        // events loops indefinitely as the listener re-runs status. Only trigger on the
        // ref-equivalent files; index / logs / objects are silently ignored.
        if (gitRelativePath.Equals("modules", StringComparison.Ordinal))
        {
            // modules/ directory itself created / deleted — submodule added or all removed.
            Schedule(_submodules);
            return;
        }
        if (gitRelativePath.StartsWith("modules/", StringComparison.Ordinal))
        {
            var afterModules = gitRelativePath.Substring("modules/".Length);
            var nextSlash = afterModules.IndexOf('/');
            if (nextSlash < 0)
            {
                // modules/<name> directory itself created / deleted — a specific submodule
                // was added or deinit'd.
                Schedule(_submodules);
                return;
            }
            var perSubmodule = afterModules.Substring(nextSlash + 1);
            if (perSubmodule.Equals("HEAD", StringComparison.Ordinal)
                || perSubmodule.Equals("packed-refs", StringComparison.Ordinal)
                || perSubmodule.StartsWith("refs/", StringComparison.Ordinal))
            {
                Schedule(_submodules);
            }
            return;
        }
        // .git/objects/**, .git/logs/**, .git/lfs/**, .git/hooks/**, .git/index — ignored.
    }

    private static readonly string DotGitSegment = Path.DirectorySeparatorChar + ".git";

    // Matches ".git" as a path segment anywhere in the path — both the repo's own
    // .git directory and any nested .git from an embedded submodule (where the
    // submodule's gitdir lives at <sub>/.git/ rather than via a gitlink file).
    // Without the nested case, the parent's recursive tree watcher fires on the
    // submodule's `.git/index.lock` churn during every `git status` / `git submodule
    // status` call, looping reload → status → lock churn → reload.
    private bool IsUnderGit(string fullPath)
    {
        var idx = 0;
        while ((idx = fullPath.IndexOf(DotGitSegment, idx, PathCmp)) >= 0)
        {
            var endIdx = idx + DotGitSegment.Length;
            if (endIdx == fullPath.Length
                || fullPath[endIdx] == Path.DirectorySeparatorChar
                || fullPath[endIdx] == Path.AltDirectorySeparatorChar)
                return true;
            idx = endIdx;
        }
        return false;
    }

    private bool IsGitmodules(string fullPath)
        => string.Equals(fullPath, _gitmodulesPath, PathCmp);

    private string? ToGitRelativePath(string fullPath)
    {
        if (!fullPath.StartsWith(_gitDirPrefix, PathCmp)) return null;
        return fullPath[_gitDirPrefix.Length..].Replace('\\', '/');
    }

    // Activity-gate: when we ourselves are running git on this repo, the writes
    // our process causes (index stat cache, per-submodule index, sometimes refs
    // and tracked-file mtimes) bubble up as FSW events. Treating those as
    // "external change" and broadcasting them retriggers the same git read,
    // which writes again, looping forever. The tracker stays "active" for the
    // git invocation plus a short tail — long enough to absorb the post-syscall
    // delivery lag. Consulted by the drain only: a real external edit arriving
    // in the window is postponed until git goes quiet, never discarded.
    private bool IsOurOwnWrite() => _activity.IsActive(_repo.Path);

    // Called under _timerLock. The coalescing window for the *next* arrival arm, scaled toward this
    // repo's own status-read cost so a burst on a slow disk coalesces into roughly one reload per
    // service-time. Reacts to a slower read immediately, relaxes only gradually, so one warm-cache
    // read doesn't collapse a genuinely slow repo's window back to the floor.
    internal int CurrentDebounceMs()
    {
        if (_readGate.LastStatusReadDuration(_repo.Id) is not { } reading)
            return DebounceFloorMs;                // cold repo: no read yet, fall back to today's default
        if (reading != _lastSample)                // a genuinely new read has landed since we last sampled
        {
            _lastSample = reading;
            var ms = reading.TotalMilliseconds;
            _smoothedReadMs = ms >= _smoothedReadMs
                ? ms                                             // attack: the disk got slower — react now
                : DecayAlpha * ms + (1 - DecayAlpha) * _smoothedReadMs;   // decay: relax slowly
        }
        return (int)Math.Clamp(_smoothedReadMs, DebounceFloorMs, DebounceCeilingMs);
    }

    // One debounce channel. Arrival always sets Pending; the activity gate can only postpone the
    // drain, never cancel it, so there is no path from "event arrived" to "nothing ever happens".
    private sealed class Channel
    {
        public Timer Debounce = null!;
        public Action<Guid> Broadcast = null!;
        public bool Pending;                    // guarded by _timerLock, like every Timer.Change here
    }

    private Channel NewChannel(Action<Guid> broadcast)
    {
        var channel = new Channel { Broadcast = broadcast };
        channel.Debounce = new Timer(_ => Drain(channel), null, Timeout.Infinite, Timeout.Infinite);
        return channel;
    }

    // The _timerLock re-check of _disposed is the authoritative one, so the Change() can't race
    // Dispose()'s timer teardown. The Volatile.Read pre-check is just a cheap fast-path.
    private void Schedule(Channel channel)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_timerLock)
        {
            if (_disposed != 0) return;
            channel.Pending = true;
            channel.Debounce.Change(CurrentDebounceMs(), Timeout.Infinite);
        }
    }

    private void Drain(Channel channel)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_timerLock)
        {
            if (_disposed != 0 || !channel.Pending) return;
            // Git is still writing: postpone, don't discard. Re-arming polls at debounce
            // granularity, bounded by the tracker's own quiet tail.
            // The re-arm polls at the fixed floor (not CurrentDebounceMs) so a parked deferral
            // notices the reopened gate promptly; only the arrival window scales with read cost.
            if (IsOurOwnWrite())
            {
                channel.Debounce.Change(DebounceFloorMs, Timeout.Infinite);
                return;
            }
            channel.Pending = false;
        }
        // Outside _timerLock: FSW threadpool callbacks contend for it, and taking the UI
        // dispatcher's queue underneath it is a deadlock shape.
        var repoId = _repo.Id;
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            channel.Broadcast(repoId);
        });
    }

    // FSW's internal buffer overflowed and events were dropped (huge churn — typically a build or
    // a checkout touching thousands of files), so nothing is known about any channel: arm all four
    // and let the UI reconcile via a full reload.
    internal void ScheduleAllChannels()
    {
        foreach (var channel in _channels)
            Schedule(channel);
    }

    private void OnError(object sender, ErrorEventArgs e) => ScheduleAllChannels();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var (watcher, onEvent, onRenamed) in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= onEvent;
            watcher.Changed -= onEvent;
            watcher.Deleted -= onEvent;
            watcher.Renamed -= onRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
            WatcherDiagnostics.Disposed();
        }
        // Dispose the timers under the lock Schedule/Drain use. _disposed is already set above,
        // so anything that takes the lock after this skips its Change(); anything holding the
        // lock mid-Change() finishes first, blocking this teardown until it's safe.
        lock (_timerLock)
        {
            foreach (var channel in _channels)
                channel.Debounce.Dispose();
        }
    }
}
