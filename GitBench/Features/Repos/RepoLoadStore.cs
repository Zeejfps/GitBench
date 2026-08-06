using GitBench.Git;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Whether git work is outstanding for a repo, from the two places work can be: a background read
// queued on or holding a permit from the shared read gate, and a push/pull/fetch in the operations
// store. One signal, so a RepoBar row spins for "git is doing something here" rather than for one
// particular mechanism.
internal interface IRepoLoadStore
{
    // True while this repo has git work outstanding. Call inside a reactive binding (rows) — the
    // underlying state read is auto-tracked, so the row updates live. Asking about a repo is also
    // what starts sampling it, so a repo with no row costs nothing.
    bool IsLoading(Guid repoId);

    // True while any repo being sampled is. Drives the RepoBar's single spinner animation, so it
    // only ticks while something is actually spinning.
    IReadable<bool> AnyLoading { get; }
}

/// <summary>
/// Projects "is git work outstanding for this repo" onto the UI thread, per repo, once per frame.
///
/// <para>It samples rather than subscribes because its two sources cannot be subscribed to as one:
/// <see cref="IGitReadGate"/> counts reads starting and finishing on background threads and is
/// deliberately not observable, while <see cref="IRepoOperationsStore"/> is. Sampling both on the
/// frame tick turns them into one reactive per-repo flag, and the only consumer — the RepoBar row
/// spinner — is redrawing every frame while it is on anyway.</para>
///
/// <para>The read gate is the useful half: every background read in the app passes through it, so a
/// read added later shows up here without being told to.</para>
/// </summary>
internal sealed class RepoLoadStore : IRepoLoadStore, IHostedService, IDisposable
{
    private readonly IGitReadGate _gate;
    private readonly IRepoOperationsStore _ops;
    private readonly IFrameTicker _ticker;
    private readonly Action<float> _tick;

    // Per-repo flag, created on first ask so a row's binding has a stable observable to subscribe to
    // before the first sample. The id list is the sample set — only repos something has asked about,
    // which is exactly the rows that exist. UI-thread only.
    private readonly Dictionary<Guid, State<bool>> _loading = new();
    private readonly List<Guid> _tracked = new();
    private readonly State<bool> _any = new(false);

    private bool _started;
    private bool _disposed;

    public IReadable<bool> AnyLoading => _any;

    public RepoLoadStore(IGitReadGate gate, IRepoOperationsStore ops, IFrameTicker ticker)
    {
        _gate = gate;
        _ops = ops;
        _ticker = ticker;
        _tick = _ => Sample();
    }

    public void Start()
    {
        if (_started) return; // idempotent
        _started = true;
        _ticker.Add(_tick);
    }

    public bool IsLoading(Guid repoId) => Slot(repoId).Value;

    private void Sample()
    {
        if (_disposed) return;
        var any = false;
        // Indexed rather than foreach: writing a slot notifies its subscribers synchronously, and a
        // row rebuilt by that notification can ask about a repo not yet tracked — which appends here.
        for (var i = 0; i < _tracked.Count; i++)
        {
            var id = _tracked[i];
            var loading = _gate.HasOutstandingReads(id) || _ops.IsBusy(id);
            var slot = _loading[id];
            // Writing an unchanged value would invalidate every row's badge every frame.
            if (slot.Value != loading) slot.Value = loading;
            any |= loading;
        }

        if (_any.Value != any) _any.Value = any;
    }

    private State<bool> Slot(Guid id)
    {
        if (!_loading.TryGetValue(id, out var s))
        {
            s = new State<bool>(false);
            _loading[id] = s;
            _tracked.Add(id);
        }
        return s;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ticker.Remove(_tick);
        foreach (var s in _loading.Values) s.Dispose();
        _loading.Clear();
        _tracked.Clear();
        _any.Dispose();
    }
}
