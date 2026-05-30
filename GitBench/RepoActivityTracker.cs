namespace GitGui;

// Per-repo "git is currently running" gate consulted by RepoWatcher before it
// schedules any debounce.
//
// Why this exists: every git read — `status`, `submodule status`, `ls-tree`,
// libgit2 Repository operations — mutates files inside `.git/` (the index
// stat cache, per-submodule index files, sometimes refs) and can touch mtimes
// in the working tree. The OS file watcher sees those writes and fires
// "external change" events, which bounce back through the message bus as
// WorkingTreeChangedMessage / SubmodulesChangedMessage, which retrigger the
// same git reads — an unbounded loop.
//
// The fix is to acknowledge the asymmetry: git reads are not idempotent on
// disk, so the watcher can't treat its own write echoes as user-driven change.
// We mark a repo "active" for the duration of every git invocation plus a
// short tail (FSW delivers events asynchronously after the process exits, so
// the gate must stay closed briefly past End). The watcher consults this and
// drops events that arrive during the window.
//
// Trade-off: a genuinely external change made by the user during the window
// is dropped at FSW level too. That's acceptable — the in-flight reload's
// `git status` will see the user's change in its output (filesystem is
// authoritative for the snapshot), and any future change reopens the gate.
public interface IRepoActivityTracker
{
    IDisposable Begin(string repoPath);
    bool IsActive(string repoPath);
}

public sealed class RepoActivityTracker : IRepoActivityTracker
{
    // FSW on Windows can deliver events 100-400ms after the writing syscall
    // returns, depending on the buffer drain. A few hundred ms of tail covers
    // the gap without making the watcher feel unresponsive to real edits.
    private const int TailMs = 500;

    private readonly Dictionary<string, State> _byPath;
    private readonly object _lock = new();

    public RepoActivityTracker()
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _byPath = new Dictionary<string, State>(cmp);
    }

    public IDisposable Begin(string repoPath)
    {
        var key = Normalize(repoPath);
        lock (_lock)
        {
            if (!_byPath.TryGetValue(key, out var s))
            {
                s = new State();
                _byPath[key] = s;
            }
            s.Count++;
        }
        return new Scope(this, key);
    }

    public bool IsActive(string repoPath)
    {
        var key = Normalize(repoPath);
        lock (_lock)
        {
            if (!_byPath.TryGetValue(key, out var s)) return false;
            if (s.Count > 0) return true;
            return Environment.TickCount64 < s.QuietAfterTick;
        }
    }

    private void End(string key)
    {
        lock (_lock)
        {
            if (!_byPath.TryGetValue(key, out var s)) return;
            s.Count = Math.Max(0, s.Count - 1);
            if (s.Count == 0)
                s.QuietAfterTick = Environment.TickCount64 + TailMs;
        }
    }

    private static string Normalize(string repoPath)
    {
        try { return Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return repoPath; }
    }

    private sealed class State
    {
        public int Count;
        public long QuietAfterTick;
    }

    private sealed class Scope : IDisposable
    {
        private readonly RepoActivityTracker _owner;
        private readonly string _key;
        private int _disposed;

        public Scope(RepoActivityTracker owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.End(_key);
        }
    }
}
