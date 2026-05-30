namespace GitGui;

public sealed class PreferencesService : IDisposable
{
    private const int SaveDebounceMs = 500;

    private readonly string _path;
    private readonly System.Threading.Timer _saveTimer;
    private readonly object _gate = new();

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

    public void SetWindowSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        Mutate(p => p with { WindowWidth = width, WindowHeight = height });
    }

    public void SetRepoBarWidth(float width) => Mutate(p => p with { RepoBarWidth = width });

    public void SetBranchesWidth(float width) => Mutate(p => p with { BranchesWidth = width });

    public void SetCommitDetailsWidth(float width) => Mutate(p => p with { CommitDetailsWidth = width });

    public void SetFileViewMode(FileViewMode mode) => Mutate(p => p with { FileViewMode = mode });

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
        Preferences snapshot;
        lock (_gate) snapshot = _current;
        try { PreferencesStore.Save(_path, snapshot); }
        catch (Exception ex) { Console.WriteLine($"Failed to save preferences: {ex.Message}"); }
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
