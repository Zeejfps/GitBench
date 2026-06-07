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

    // Raised after any add/update/remove (after the list is mutated). No payload — listeners
    // re-read Profiles. Used to flush GitIdentityService's resolution memo.
    public event Action? Changed;

    public IdentityProfileService(IReadOnlyList<IdentityProfile> initial, string path)
    {
        _path = path;
        foreach (var p in initial) Profiles.Add(p);
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
        Changed?.Invoke();
        _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
    }

    private void Flush()
    {
        lock (_writeLock)
        {
            var snapshot = Profiles.ToList();
            try { IdentityProfileStore.Save(_path, snapshot); }
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
