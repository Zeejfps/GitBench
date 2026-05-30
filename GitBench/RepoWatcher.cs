using ZGF.Observable;

namespace GitGui;

// Watches a single repository's filesystem for changes the user makes outside the
// app (editor saves, terminal `git` commands, builds, IDE checkouts, …) and turns
// them into the same MessageBus signals the in-app presenters already use.
//
// Two watchers per repo:
//   * Working tree (root, recursive, excluding .git): edits → WorkingTreeChangedMessage.
//   * .git directory: HEAD/refs/packed-refs/FETCH_HEAD/ORIG_HEAD/MERGE_HEAD → RefsChangedMessage.
//
// FSW fires events on threadpool threads in storms (a single editor save can be 3-5
// events; a build or git checkout can be thousands), so we debounce per channel and
// post the final broadcast through IUiDispatcher onto the UI thread.
//
// Design note — feedback loop avoidance:
//   We intentionally do NOT call libgit2 inside the debounce callback (e.g. to hash
//   a status snapshot and suppress no-op broadcasts). libgit2's RetrieveStatus updates
//   `.git/index`'s stat cache as a side effect, which fires our own `.git` watcher, which
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
    private const int DebounceMs = 250;
    private const int FswBufferBytes = 64 * 1024;

    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Repo _repo;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly IRepoActivityTracker _activity;

    private readonly FileSystemWatcher? _treeWatcher;
    private readonly FileSystemWatcher? _gitWatcher;
    private readonly Timer _workingTreeDebounce;
    private readonly Timer _refsDebounce;
    private readonly Timer _worktreesDebounce;
    private readonly Timer _submodulesDebounce;

    private readonly string _gitDirPrefix;
    private readonly string _gitmodulesPath;

    private int _disposed;

    public RepoWatcher(Repo repo, IUiDispatcher dispatcher, IMessageBus bus, IRepoActivityTracker activity)
    {
        _repo = repo;
        _dispatcher = dispatcher;
        _bus = bus;
        _activity = activity;
        _gitDirPrefix = Path.Combine(repo.Path, ".git") + Path.DirectorySeparatorChar;
        _gitmodulesPath = Path.Combine(repo.Path, ".gitmodules");

        _workingTreeDebounce = new Timer(_ => OnWorkingTreeDebounce(), null, Timeout.Infinite, Timeout.Infinite);
        _refsDebounce = new Timer(_ => OnRefsDebounce(), null, Timeout.Infinite, Timeout.Infinite);
        _worktreesDebounce = new Timer(_ => OnWorktreesDebounce(), null, Timeout.Infinite, Timeout.Infinite);
        _submodulesDebounce = new Timer(_ => OnSubmodulesDebounce(), null, Timeout.Infinite, Timeout.Infinite);

        _treeWatcher = TryCreateWatcher(repo.Path);
        if (_treeWatcher != null)
        {
            _treeWatcher.Created += OnTreeEvent;
            _treeWatcher.Changed += OnTreeEvent;
            _treeWatcher.Deleted += OnTreeEvent;
            _treeWatcher.Renamed += OnTreeRenamed;
            _treeWatcher.Error += OnError;
        }

        var gitDir = Path.Combine(repo.Path, ".git");
        if (Directory.Exists(gitDir))
        {
            _gitWatcher = TryCreateWatcher(gitDir);
            if (_gitWatcher != null)
            {
                _gitWatcher.Created += OnGitEvent;
                _gitWatcher.Changed += OnGitEvent;
                _gitWatcher.Deleted += OnGitEvent;
                _gitWatcher.Renamed += OnGitRenamed;
                _gitWatcher.Error += OnError;
            }
        }
    }

    private static FileSystemWatcher? TryCreateWatcher(string path)
    {
        try
        {
            return new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.CreationTime,
                InternalBufferSize = FswBufferBytes,
                EnableRaisingEvents = true,
            };
        }
        catch
        {
            // Best-effort: a repo on a disconnected drive, an unreadable path, etc.
            // We just won't notice external changes for this repo. The user can still
            // refresh by switching repos or performing an in-app op.
            return null;
        }
    }

    private void OnTreeEvent(object sender, FileSystemEventArgs e)
    {
        if (IsUnderGit(e.FullPath)) return;
        // .gitmodules lives in the working tree, but edits to it specifically should
        // re-run submodule discovery rather than just bumping the WorkingTree channel
        // (which would only re-read GetLocalChanges, not the submodule list).
        if (IsGitmodules(e.FullPath))
            ScheduleSubmodules();
        ScheduleWorkingTree();
    }

    private void OnTreeRenamed(object sender, RenamedEventArgs e)
    {
        if (IsGitmodules(e.FullPath) || IsGitmodules(e.OldFullPath))
            ScheduleSubmodules();
        if (IsUnderGit(e.FullPath) && IsUnderGit(e.OldFullPath)) return;
        ScheduleWorkingTree();
    }

    private void OnGitEvent(object sender, FileSystemEventArgs e)
        => ClassifyGitChange(ToGitRelativePath(e.FullPath));

    private void OnGitRenamed(object sender, RenamedEventArgs e)
    {
        ClassifyGitChange(ToGitRelativePath(e.FullPath));
        ClassifyGitChange(ToGitRelativePath(e.OldFullPath));
    }

    private void ClassifyGitChange(string? gitRelativePath)
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
            ScheduleRefs();
            return;
        }

        // .git/worktrees/<name>/ holds per-worktree HEAD/ORIG_HEAD/REBASE_HEAD plus the
        // directory itself is created/deleted on `git worktree add`/`remove`. Any change
        // here invalidates the worktree set or a worktree's HEAD; the sync service will
        // re-run discovery and fan refs out to children.
        if (gitRelativePath.StartsWith("worktrees/", StringComparison.Ordinal)
            || gitRelativePath.Equals("worktrees", StringComparison.Ordinal))
        {
            ScheduleWorktrees();
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
            ScheduleSubmodules();
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
                ScheduleSubmodules();
                return;
            }
            var perSubmodule = afterModules.Substring(nextSlash + 1);
            if (perSubmodule.Equals("HEAD", StringComparison.Ordinal)
                || perSubmodule.Equals("packed-refs", StringComparison.Ordinal)
                || perSubmodule.StartsWith("refs/", StringComparison.Ordinal))
            {
                ScheduleSubmodules();
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
    // delivery lag. Real external edits during the window are dropped at this
    // gate, but their effect is already captured in the in-flight reload's
    // git-status snapshot, so the resulting UI state stays correct.
    private bool IsOurOwnWrite() => _activity.IsActive(_repo.Path);

    private void ScheduleWorkingTree()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (IsOurOwnWrite()) return;
        _workingTreeDebounce.Change(DebounceMs, Timeout.Infinite);
    }

    private void ScheduleRefs()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (IsOurOwnWrite()) return;
        _refsDebounce.Change(DebounceMs, Timeout.Infinite);
    }

    private void ScheduleWorktrees()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (IsOurOwnWrite()) return;
        _worktreesDebounce.Change(DebounceMs, Timeout.Infinite);
    }

    private void ScheduleSubmodules()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (IsOurOwnWrite()) return;
        _submodulesDebounce.Change(DebounceMs, Timeout.Infinite);
    }

    private void OnWorkingTreeDebounce()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var repoId = _repo.Id;
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _bus.Broadcast(new WorkingTreeChangedMessage(repoId));
        });
    }

    private void OnRefsDebounce()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var repoId = _repo.Id;
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _bus.Broadcast(new RefsChangedMessage(repoId));
        });
    }

    private void OnWorktreesDebounce()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var repoId = _repo.Id;
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _bus.Broadcast(new WorktreesChangedMessage(repoId));
        });
    }

    private void OnSubmodulesDebounce()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var repoId = _repo.Id;
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _bus.Broadcast(new SubmodulesChangedMessage(repoId));
        });
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Internal buffer overflowed and events were dropped (huge churn — typically a
        // build or a checkout touching thousands of files). Schedule every channel so
        // the UI reconciles via a full reload rather than staying stale.
        ScheduleWorkingTree();
        ScheduleRefs();
        ScheduleWorktrees();
        ScheduleSubmodules();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_treeWatcher != null)
        {
            _treeWatcher.EnableRaisingEvents = false;
            _treeWatcher.Created -= OnTreeEvent;
            _treeWatcher.Changed -= OnTreeEvent;
            _treeWatcher.Deleted -= OnTreeEvent;
            _treeWatcher.Renamed -= OnTreeRenamed;
            _treeWatcher.Error -= OnError;
            _treeWatcher.Dispose();
        }
        if (_gitWatcher != null)
        {
            _gitWatcher.EnableRaisingEvents = false;
            _gitWatcher.Created -= OnGitEvent;
            _gitWatcher.Changed -= OnGitEvent;
            _gitWatcher.Deleted -= OnGitEvent;
            _gitWatcher.Renamed -= OnGitRenamed;
            _gitWatcher.Error -= OnError;
            _gitWatcher.Dispose();
        }
        _workingTreeDebounce.Dispose();
        _refsDebounce.Dispose();
        _worktreesDebounce.Dispose();
        _submodulesDebounce.Dispose();
    }
}
