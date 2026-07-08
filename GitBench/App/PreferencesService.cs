using GitBench.Features.LocalChanges;
using GitBench.Localization;
using GitBench.Theming;

namespace GitBench.App;

public sealed class PreferencesService : IDisposable
{
    private const int SaveDebounceMs = 500;

    private readonly string _path;
    private readonly System.Threading.Timer _saveTimer;
    private readonly object _gate = new();
    // Serializes the actual file write. A timer-fired Flush can overlap the synchronous Flush in
    // Dispose (Timer.Dispose doesn't wait for an in-flight callback), and both would otherwise
    // write the file concurrently.
    private readonly object _writeLock = new();

    private Preferences _current;
    private bool _disposed;

    public PreferencesService(Preferences initial, string path)
    {
        _current = initial;
        _path = path;
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Preferences Current
    {
        get { lock (_gate) return _current; }
    }

    public void SetTheme(ThemeMode mode) => Mutate(p => p with { Theme = mode });

    public void SetLanguage(Locale language) => Mutate(p => p with { Language = language });

    public void SetWindowSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        Mutate(p => p with { WindowWidth = width, WindowHeight = height });
    }

    public void SetWindowPosition(int x, int y) => Mutate(p => p with { WindowX = x, WindowY = y });

    public void SetReviewWindowSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        Mutate(p => p with { ReviewWindowWidth = width, ReviewWindowHeight = height });
    }

    public void SetRepoBarWidth(float width) => Mutate(p => p with { RepoBarWidth = width });

    public void SetBranchesWidth(float width) => Mutate(p => p with { BranchesWidth = width });

    public void SetCommitDetailsWidth(float width) => Mutate(p => p with { CommitDetailsWidth = width });

    public void SetCommitDetailsSplitFraction(float fraction) => Mutate(p => p with { CommitDetailsSplitFraction = fraction });

    public void SetFileViewMode(FileViewMode mode) => Mutate(p => p with { FileViewMode = mode });

    public void SetHideRemoteOnlyBranches(bool hide) => Mutate(p => p with { HideRemoteOnlyBranches = hide });

    private void Mutate(Func<Preferences, Preferences> mutator)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var next = mutator(_current);
            if (next == _current) return;
            _current = next;
            _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        // Hold _writeLock across the whole read-and-write so concurrent flushes serialize; reading
        // the snapshot inside the lock means the last writer persists the freshest state rather
        // than a stale snapshot captured before it blocked.
        lock (_writeLock)
        {
            Preferences snapshot;
            lock (_gate) snapshot = _current;
            try { PreferencesStore.Save(_path, snapshot); }
            catch (Exception ex) { Console.WriteLine($"Failed to save preferences: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        _saveTimer.Dispose();
        Flush();
    }
}
