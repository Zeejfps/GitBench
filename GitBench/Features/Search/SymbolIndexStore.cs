using GitBench.Features.CodeIntel;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Search;

/// <summary>The one place a repository's symbol index lives. Everything else reads it from here.</summary>
internal interface ISymbolIndexStore
{
    /// <summary>The active repository's symbols as far as its index has got; swaps on repo switch,
    /// and is republished as a build lands files.</summary>
    IReadable<SymbolIndexSnapshot> Active { get; }
}

/// <summary>
/// Owns a symbol index per repository for the app session, built in the background the first time
/// the repository is active and refreshed when its working tree changes.
/// </summary>
/// <remarks>
/// <para>
/// Listing the files runs under the git read gate at sweep priority, so a first build waits behind
/// the reads the active repository's first load needs; parsing then runs outside the gate.
/// </para>
/// <para>
/// A change to a repository that is not active only marks its index stale: re-listing and re-statting
/// every file of every open repository on each reconcile tick would cost more than it serves, and
/// search only ever reads the active one. It is refreshed when it becomes active again.
/// </para>
/// </remarks>
internal sealed class SymbolIndexStore : ISymbolIndexStore, IHostedService, IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitRepositoryReader _git;
    private readonly ISymbolExtractor _extractor;
    private readonly StartupSweepCoordinator _sweep;
    private readonly IMessageBus _bus;
    private readonly IUiDispatcher _dispatcher;

    private readonly Dictionary<Guid, Slot> _slots = new();
    private readonly State<SymbolIndexSnapshot> _active = new(SymbolIndexSnapshot.None);
    private readonly List<IDisposable> _subscriptions = new();
    private bool _started;
    private bool _disposed;

    public SymbolIndexStore(
        IRepoRegistry registry,
        IGitRepositoryReader git,
        ISymbolExtractor extractor,
        StartupSweepCoordinator sweep,
        IMessageBus bus,
        IUiDispatcher dispatcher)
    {
        _registry = registry;
        _git = git;
        _extractor = extractor;
        _sweep = sweep;
        _bus = bus;
        _dispatcher = dispatcher;
    }

    public IReadable<SymbolIndexSnapshot> Active => _active;

    /// <summary>One repository's index and where its build stands. Touched on the UI thread only,
    /// except <see cref="Index"/>, which only the one running build touches.</summary>
    private sealed class Slot(Repo repo, RepoSymbolIndex index)
    {
        public Repo Repo { get; } = repo;
        public RepoSymbolIndex Index { get; } = index;
        public CancellationTokenSource Cancel { get; } = new();
        public SymbolIndexSnapshot Latest { get; set; } = new(repo.Id, [], new SymbolIndexProgress.Listing());
        public BuildState State { get; set; } = BuildState.Idle;
        public bool Stale { get; set; }
    }

    private enum BuildState
    {
        Idle,
        Running,

        /// <summary>Running, and asked for again: another build follows this one.</summary>
        RunningThenAgain,
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _subscriptions.Add(_registry.Active.Subscribe(_ => OnActiveChanged()));
        _subscriptions.Add(_registry.Repos.Subscribe(_ => DropClosedRepos()));
        _subscriptions.Add(_bus.SubscribeScoped<WorkingTreeChangedMessage>(OnWorkingTreeChanged));
    }

    private void OnActiveChanged()
    {
        if (_disposed) return;
        if (_registry.Active.Value is not { } repo)
        {
            _active.Value = SymbolIndexSnapshot.None;
            return;
        }

        if (!_slots.TryGetValue(repo.Id, out var slot))
        {
            slot = new Slot(repo, new RepoSymbolIndex(repo.Id, repo.Path, _extractor));
            _slots[repo.Id] = slot;
            Build(slot);
        }
        else if (slot.Stale)
        {
            Build(slot);
        }

        _active.Value = slot.Latest;
    }

    private void OnWorkingTreeChanged(WorkingTreeChangedMessage message)
    {
        if (_disposed || message.IndexOnly) return;
        if (!_slots.TryGetValue(message.RepoId, out var slot)) return;

        if (_registry.Active.Value?.Id == message.RepoId) Build(slot);
        else slot.Stale = true;
    }

    private void Build(Slot slot)
    {
        slot.Stale = false;
        switch (slot.State)
        {
            case BuildState.Running:
                slot.State = BuildState.RunningThenAgain;
                return;
            case BuildState.RunningThenAgain:
                return;
            case BuildState.Idle:
                break;
            default:
                throw new InvalidOperationException($"No rule for {slot.State}.");
        }

        slot.State = BuildState.Running;
        var cancel = slot.Cancel.Token;
        _sweep.RunThrottled(slot.Repo.Id, () =>
        {
            var listed = List(slot.Repo);
            Task.Factory.StartNew(
                    () => slot.Index.Refresh(listed, snapshot => _dispatcher.Post(() => Land(slot, snapshot)), cancel),
                    cancel,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .ContinueWith(_ => _dispatcher.Post(() => Finished(slot)), TaskScheduler.Default);
        });
    }

    private IReadOnlyList<string> List(Repo repo)
    {
        try
        {
            return _git.ListWorkingTreeFiles(repo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SymbolIndex] Listing {repo.DisplayName} failed: {ex.Message}");
            return [];
        }
    }

    private void Land(Slot slot, SymbolIndexSnapshot snapshot)
    {
        if (_disposed || slot.Cancel.IsCancellationRequested) return;
        slot.Latest = snapshot;
        if (_registry.Active.Value?.Id == slot.Repo.Id) _active.Value = snapshot;
    }

    private void Finished(Slot slot)
    {
        if (_disposed || slot.Cancel.IsCancellationRequested) return;
        var again = slot.State == BuildState.RunningThenAgain;
        slot.State = BuildState.Idle;
        if (again) Build(slot);
    }

    private void DropClosedRepos()
    {
        if (_disposed || _slots.Count == 0) return;
        var open = _registry.Repos.Select(r => r.Id).ToHashSet();
        foreach (var id in _slots.Keys.Where(id => !open.Contains(id)).ToArray())
        {
            _slots[id].Cancel.Cancel();
            _slots.Remove(id);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        foreach (var slot in _slots.Values) slot.Cancel.Cancel();
        _slots.Clear();
    }
}
