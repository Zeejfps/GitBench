using ZGF.Observable;

namespace GitBench;

// Owns the global list of identity profiles: an ObservableList the manager dialog binds to
// directly, plus a debounced save mirroring PreferencesService. Mutations (Add/Update/Remove)
// update the list and schedule a write; Changed fires so the resolver can flush its memo and
// the status chip can refresh.
public sealed class IdentityProfileService : IDisposable
{
    private const int SaveDebounceMs = 500;

    private readonly string _path;
    private readonly System.Threading.Timer _saveTimer;
    private readonly object _writeLock = new();
    private bool _disposed;

    public ObservableList<IdentityProfile> Profiles { get; } = new();

    // Immutable copy of Profiles, rebuilt on the UI thread after every mutation. The resolver reads
    // it from background git threads and Flush() serializes it from the timer thread — so the live
    // ObservableList (not thread-safe) is only ever touched on the UI thread.
    private volatile IReadOnlyList<IdentityProfile> _snapshot = Array.Empty<IdentityProfile>();
    public IReadOnlyList<IdentityProfile> Snapshot => _snapshot;

    // Raised after any add/update/remove (after the list is mutated). No payload — listeners
    // re-read Profiles. Used to flush GitIdentityService's resolution memo.
    public event Action? Changed;

    public IdentityProfileService(IReadOnlyList<IdentityProfile> initial, string path)
    {
        _path = path;
        foreach (var p in initial) Profiles.Add(p);
        _snapshot = Profiles.ToArray();
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Add(IdentityProfile profile)
    {
        Profiles.Add(profile);
        AfterMutate();
    }

    public void Update(IdentityProfile profile)
    {
        for (var i = 0; i < Profiles.Count; i++)
        {
            if (Profiles[i].Id == profile.Id)
            {
                Profiles.Replace(i, profile);
                AfterMutate();
                return;
            }
        }
    }

    public void Remove(Guid id)
    {
        for (var i = 0; i < Profiles.Count; i++)
        {
            if (Profiles[i].Id == id)
            {
                Profiles.RemoveAt(i);
                AfterMutate();
                return;
            }
        }
    }

    private void AfterMutate()
    {
        if (_disposed) return;
        _snapshot = Profiles.ToArray();
        Changed?.Invoke();
        _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
    }

    private void Flush()
    {
        lock (_writeLock)
        {
            try { IdentityProfileStore.Save(_path, _snapshot); }
            catch (Exception ex) { Console.WriteLine($"Failed to save identity profiles: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Dispose();
        Flush();
    }
}
