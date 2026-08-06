using System.Collections.Concurrent;
using GitBench.Features.Repos;
using GitBench.Git;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// The RepoBar row spinner needs one per-repo answer to "is git doing something here", and the two
// things that can be doing it report differently: the read gate counts reads on background threads
// and isn't observable, the operations store is. These pin that the store folds both into one
// reactive flag, that it only samples repos something has asked about, and that AnyLoading — which
// decides whether the bar's animation ticks at all — follows the whole set.
public sealed class RepoLoadStoreTests
{
    [Fact]
    public void A_repo_with_an_outstanding_read_is_loading()
    {
        using var h = new Harness();
        var repo = Guid.NewGuid();
        h.Track(repo);

        h.Gate.SetOutstanding(repo, true);
        h.Ticker.Tick();

        Assert.True(h.Store.IsLoading(repo));
    }

    // A fetch is not a read and never touches the gate, but it is the thing the user most expects a
    // spinner for — the row must not go still while one is running.
    [Fact]
    public void A_repo_with_a_remote_operation_in_flight_is_loading()
    {
        using var h = new Harness();
        var repo = Guid.NewGuid();
        h.Track(repo);

        h.Ops.SetBusy(repo, true);
        h.Ticker.Tick();

        Assert.True(h.Store.IsLoading(repo));
    }

    [Fact]
    public void A_repo_with_neither_is_not_loading()
    {
        using var h = new Harness();
        var repo = Guid.NewGuid();
        h.Track(repo);

        h.Ticker.Tick();

        Assert.False(h.Store.IsLoading(repo));
    }

    [Fact]
    public void A_finished_read_clears_the_flag_on_the_next_frame()
    {
        using var h = new Harness();
        var repo = Guid.NewGuid();
        h.Track(repo);
        h.Gate.SetOutstanding(repo, true);
        h.Ticker.Tick();
        Assert.True(h.Store.IsLoading(repo));

        h.Gate.SetOutstanding(repo, false);
        h.Ticker.Tick();

        Assert.False(h.Store.IsLoading(repo));
    }

    // One repo loading is enough to keep the bar's single animation running, and it must stop once
    // the last one lands — an idle spinner ticking every frame is the cost of getting this wrong.
    [Fact]
    public void AnyLoading_follows_whether_any_tracked_repo_is()
    {
        using var h = new Harness();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        h.Track(a);
        h.Track(b);

        h.Ticker.Tick();
        Assert.False(h.Store.AnyLoading.Value);

        h.Gate.SetOutstanding(b, true);
        h.Ticker.Tick();
        Assert.True(h.Store.AnyLoading.Value);

        h.Gate.SetOutstanding(b, false);
        h.Ticker.Tick();
        Assert.False(h.Store.AnyLoading.Value);
    }

    // Sampling is driven by what has been asked about, so a repo with no row costs nothing — and,
    // more importantly, can't hold the bar's animation on for work the user cannot see.
    [Fact]
    public void A_repo_nothing_has_asked_about_is_never_sampled()
    {
        using var h = new Harness();
        var unseen = Guid.NewGuid();

        h.Gate.SetOutstanding(unseen, true);
        h.Ticker.Tick();

        Assert.False(h.Store.AnyLoading.Value);
        Assert.Equal(0, h.Gate.Queries(unseen));
    }

    // A row asking mid-sample appends to the set being iterated; that must not throw, and the new
    // repo must be sampled from the next frame.
    [Fact]
    public void A_repo_first_asked_about_during_a_sample_is_picked_up()
    {
        using var h = new Harness();
        var first = Guid.NewGuid();
        var late = Guid.NewGuid();
        h.Track(first);
        h.Gate.SetOutstanding(first, true);
        h.Gate.SetOutstanding(late, true);

        // Stands in for a row rebuilt by the flag it just watched change, which then asks about a
        // repo the store has never sampled.
        using var reentrant = h.Store.AnyLoading.Subscribe(_ => h.Store.IsLoading(late));

        Assert.Null(Record.Exception(h.Ticker.Tick));
        h.Ticker.Tick();

        Assert.True(h.Store.IsLoading(late));
    }

    private sealed class Harness : IDisposable
    {
        public readonly FakeGate Gate = new();
        public readonly FakeOperations Ops = new();
        public readonly ManualTicker Ticker = new();
        public readonly RepoLoadStore Store;

        public Harness()
        {
            Store = new RepoLoadStore(Gate, Ops, Ticker);
            Store.Start();
        }

        // Asking is what starts sampling a repo, the same way a row's badge binding does.
        public void Track(Guid repoId) => Store.IsLoading(repoId);

        public void Dispose() => Store.Dispose();
    }

    private sealed class ManualTicker : IFrameTicker
    {
        private readonly List<Action<float>> _ticks = new();

        public void Add(Action<float> tick) => _ticks.Add(tick);
        public void Remove(Action<float> tick) => _ticks.Remove(tick);

        public void Tick()
        {
            foreach (var tick in _ticks.ToArray()) tick(1f / 60f);
        }
    }

    private sealed class FakeGate : IGitReadGate
    {
        private readonly ConcurrentDictionary<Guid, bool> _outstanding = new();
        private readonly ConcurrentDictionary<Guid, int> _queries = new();

        public void SetOutstanding(Guid repoId, bool value) => _outstanding[repoId] = value;
        public int Queries(Guid repoId) => _queries.TryGetValue(repoId, out var n) ? n : 0;

        public bool HasOutstandingReads(Guid repoId)
        {
            _queries.AddOrUpdate(repoId, 1, static (_, n) => n + 1);
            return _outstanding.TryGetValue(repoId, out var v) && v;
        }

        public Task<IGitReadGate.Permit> Acquire(Guid repoId, GitReadKind kind)
            => Task.FromResult(new IGitReadGate.Permit(() => { }));

        public void SetForegroundRepo(Guid? repoId) { }

        public TimeSpan? LastStatusReadDuration(Guid repoId) => null;
    }

    private sealed class FakeOperations : IRepoOperationsStore
    {
        private readonly State<RepoOperations> _active = new(RepoOperations.Idle);
        private readonly HashSet<Guid> _busy = new();

        public void SetBusy(Guid repoId, bool busy)
        {
            if (busy) _busy.Add(repoId);
            else _busy.Remove(repoId);
        }

        public IReadable<RepoOperations> Active => _active;
        public bool HasUnseenError(Guid repoId) => false;
        public bool IsBusy(Guid repoId) => _busy.Contains(repoId);
        public void Push(Repo repo, bool force = false) { }
        public void Pull(Repo repo, PullStrategy? strategy = null) { }
        public void Fetch(Repo repo) { }
        public Task<RemoteOpResult> PullAsync(Repo repo, PullStrategy? strategy = null) => Task.FromResult(RemoteOpResult.Ok);
        public Task<RemoteOpResult> FetchAsync(Repo repo) => Task.FromResult(RemoteOpResult.Ok);
    }
}
