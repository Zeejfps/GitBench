using System.Diagnostics;
using GitBench.Features.Commits;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// One repo's in-flight index moves — the paths whose stage/unstage git has not finished or a
// snapshot has not yet reconciled — plus a failure the user has not seen because it happened while
// the repo was not active. Projected by LocalChangesViewModel into its file lists and, per repo, by
// the RepoBar error badge.
public sealed record IndexOperations(IReadOnlySet<string> PendingPaths, PendingOperationError? PendingError)
{
    private static readonly IReadOnlySet<string> NoPaths = new HashSet<string>();
    public static readonly IndexOperations Idle = new(NoPaths, null);
}

// Which channel a move's mutation owes a revalidation on: a plain index op moves content between
// HEAD and the index; a conflict resolution can also rewrite the file on disk.
public enum IndexMoveEffect { Index, WorkingTree }

// The part of a request a snapshot has caught up with: every path of it landed so far, in request
// order, and the side they moved to. The caller's selection follows these while it is still on the
// request's own rows — Requested is the whole request, for that check.
public sealed record LandedMove(IReadOnlyList<string> Paths, DiffSide ToSide, IReadOnlySet<string> Requested);

// Single source of truth for in-flight index moves (stage / unstage / mark-resolved), keyed by repo
// id so a move started on one repo keeps its rows marked — and lands on the right lists — after the
// user switches away and back. Mirrors RepoOperationsStore's shape: per-repo state, an "active"
// projection that swaps on repo switch, and a Start() that wires the registry once the UI loop exists.
public interface IRepoIndexOperationsStore
{
    // The active repo's in-flight moves. Swaps instantly on repo switch.
    IReadable<IndexOperations> Active { get; }

    // True when this repo has an index-op failure the user hasn't seen yet (it happened while the
    // repo wasn't active). Feeds the RepoBar error badge via IRepoStatusStore. Call inside a reactive
    // binding — the underlying per-repo state read is auto-tracked.
    bool HasUnseenError(Guid repoId);

    // Marks the paths in flight and runs the git op off-thread, in chunks, so a large request lands
    // in visible waves rather than all at once. Paths already in flight are dropped rather than
    // queued behind themselves — the pending op will land them, and a second `git add` of the same
    // path is a no-op that only serializes behind the first. `work` runs once per chunk with that
    // chunk's paths. A failure stops the request; it is shown as the operation-error dialog when the
    // repo is active, otherwise it waits as the repo's unseen-error badge.
    void Run(
        Repo repo,
        IReadOnlyList<string> paths,
        DiffSide toSide,
        Func<IReadOnlyList<string>, GitOutcome> work,
        Func<Strings, string> failureTitle,
        IndexMoveEffect effect = IndexMoveEffect.Index);

    // Reconciles the repo's finished chunks against a freshly applied snapshot and returns the
    // request most recently caught up with, whose landed paths the caller's selection follows.
    // Null when nothing landed.
    LandedMove? Settle(Guid repoId, IReadOnlyList<FileChange> unstaged, IReadOnlyList<FileChange> staged);
}

/// <summary>
/// Owns the stage / unstage lifecycle per repo: which paths are in flight, when their git op is
/// done, and when a snapshot has caught up with it. The state is keyed by repo id instead of held
/// on the view model that shows the active repo, so a Stage All on repo A keeps its rows marked
/// while the user looks at repo B, and the completion lands on A's state no matter which repo is
/// active when it arrives.
///
/// A request runs as a sequence of chunks on one background chain per repo, each followed by a
/// revalidation so its rows move while the rest keep spinning. Chunks are sized by time: every
/// <c>git add</c> rewrites the whole index, so a fixed small chunk is expensive on a big repo, and
/// a fixed large one hides progress on a small one.
///
/// A finished chunk settles only against a snapshot that shows its effect — every path on the side
/// it moved to, or gone from the side it left. On switch-back the snapshot store pushes the cached
/// pre-op snapshot before the fresh reload lands; settling on that would un-mark the rows on their
/// old side until the reload moved them.
/// </summary>
internal sealed class RepoIndexOperationsStore : IRepoIndexOperationsStore, IHostedService, IDisposable
{
    internal const int InitialChunkSize = 25;
    internal const int MaxChunkSize = 2000;
    private const int MaxChunkGrowth = 8;
    internal static readonly TimeSpan TargetChunkDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MinMeasuredDuration = TimeSpan.FromMilliseconds(1);

    // A revalidation costs one `git status`, and the snapshot store does not coalesce them: an
    // intermediate chunk broadcasts only once the previous reload has landed (a snapshot reached
    // Settle) or has been outstanding this long. A repo that is not active never reaches Settle, so
    // the timeout is what refreshes its warm cache while its request runs.
    internal static readonly TimeSpan ReloadTimeout = TimeSpan.FromSeconds(2);

    // A snapshot that never shows a chunk's effect (a stage that left the file identical to HEAD on
    // both sides, say) must not pin its rows in flight forever: after this many snapshots the chunk
    // settles regardless.
    private const int SettleFallbackSnapshots = 2;

    private readonly IRepoRegistry _registry;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly IUiDispatcher _dispatcher;
    private bool _started;
    // Read by the background chains between chunks, written on the UI thread.
    private volatile bool _disposed;

    // Per-repo source of truth, created lazily on first touch and kept for the app's lifetime.
    // UI-thread only — no locking needed.
    private readonly Dictionary<Guid, RepoMoves> _repos = new();

    private readonly State<IndexOperations> _active = new(IndexOperations.Idle);
    private IDisposable? _activeInner;
    private IDisposable? _activeSub;

    public IReadable<IndexOperations> Active => _active;

    public RepoIndexOperationsStore(IRepoRegistry registry, IMessageBus bus, ILocalizationService loc, IUiDispatcher dispatcher)
    {
        _registry = registry;
        _bus = bus;
        _loc = loc;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());
    }

    public bool HasUnseenError(Guid repoId) => Get(repoId).State.Value.PendingError != null;

    public void Run(
        Repo repo,
        IReadOnlyList<string> paths,
        DiffSide toSide,
        Func<IReadOnlyList<string>, GitOutcome> work,
        Func<Strings, string> failureTitle,
        IndexMoveEffect effect = IndexMoveEffect.Index)
    {
        var moves = Get(repo.Id);
        paths = moves.ExcludePending(paths);
        if (paths.Count == 0) return;

        var effects = effect switch
        {
            IndexMoveEffect.Index => MutationEffects.Index(_bus, repo.Id, paths.Count == 1 ? paths[0] : null),
            IndexMoveEffect.WorkingTree => MutationEffects.WorkingTree(_bus, repo.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, null),
        };

        var request = new IndexRequest(paths, toSide, effects, failureTitle);
        moves.Add(request);
        moves.Chain = RunAfter(moves.Chain, repo, request, work);
    }

    // The repo's chain: a request starts once the one before it has run its last chunk, so two
    // requests never race for GitService's local-state lock from parallel tasks.
    private async Task RunAfter(Task previous, Repo repo, IndexRequest request, Func<IReadOnlyList<string>, GitOutcome> work)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* a chain failure is reported by the request that failed, not by the next one */ }
        await Task.Run(() => RunChunks(repo, request, work)).ConfigureAwait(false);
    }

    private void RunChunks(Repo repo, IndexRequest request, Func<IReadOnlyList<string>, GitOutcome> work)
    {
        var all = request.Paths;
        var size = InitialChunkSize;
        var offset = 0;
        while (offset < all.Count)
        {
            if (_disposed) return;

            var count = Math.Min(size, all.Count - offset);
            var chunkPaths = new List<string>(count);
            for (var i = 0; i < count; i++) chunkPaths.Add(all[offset + i]);
            offset += count;
            var isLast = offset >= all.Count;

            var started = Stopwatch.GetTimestamp();
            GitOutcome outcome;
            try
            {
                outcome = work(chunkPaths);
            }
            catch (Exception ex)
            {
                outcome = new GitOutcome.Failed(ex.Message);
            }
            var elapsed = Stopwatch.GetElapsedTime(started);

            if (outcome is GitOutcome.Failed failed)
            {
                _dispatcher.Post(() => FailRequest(repo, request, failed));
                return;
            }

            var chunk = new IndexChunk(chunkPaths);
            _dispatcher.Post(() => CompleteChunk(repo, request, chunk, isLast));
            size = NextChunkSize(size, elapsed);
        }
    }

    private static int NextChunkSize(int previous, TimeSpan elapsed)
    {
        if (elapsed < MinMeasuredDuration) elapsed = MinMeasuredDuration;
        var scaled = previous * (TargetChunkDuration / elapsed);
        var grown = Math.Min(scaled, previous * (double)MaxChunkGrowth);
        return (int)Math.Clamp(grown, InitialChunkSize, MaxChunkSize);
    }

    // A successful chunk keeps its rows marked until a snapshot reconciles the lists — clearing at
    // this point would un-dim the rows a beat before they jump sides. The request's last chunk
    // always asks for that snapshot; an intermediate one only when no reload is already on its way.
    private void CompleteChunk(Repo repo, IndexRequest request, IndexChunk chunk, bool isLast)
    {
        if (_disposed) return;
        var moves = Get(repo.Id);
        if (!moves.Contains(request)) return;

        request.Finish(chunk);
        if (isLast || !moves.IsReloadOutstanding())
        {
            moves.MarkReloadRequested();
            request.Effects.Broadcast();
        }
    }

    // A failed chunk leaves the rows where they were and takes the rest of the request with it, so
    // every in-flight mark of the request clears now. The revalidation still fires: a chunk that
    // failed partway may have moved part of the index.
    private void FailRequest(Repo repo, IndexRequest request, GitOutcome.Failed failed)
    {
        if (_disposed) return;
        var moves = Get(repo.Id);
        if (!moves.Remove(request)) return;

        var title = request.FailureTitle(_loc.Strings.Value);
        if (_registry.Active.Value?.Id == repo.Id)
            _bus.Broadcast(new ShowOperationErrorMessage(title, failed.Message));
        else
            moves.SetPendingError(new PendingOperationError(title, failed.Message));

        moves.MarkReloadRequested();
        request.Effects.Broadcast();
    }

    public LandedMove? Settle(Guid repoId, IReadOnlyList<FileChange> unstaged, IReadOnlyList<FileChange> staged)
        => _repos.TryGetValue(repoId, out var moves) ? moves.Settle(unstaged, staged) : null;

    private RepoMoves Get(Guid id)
    {
        if (!_repos.TryGetValue(id, out var moves))
        {
            moves = new RepoMoves();
            _repos[id] = moves;
        }
        return moves;
    }

    private void OnActiveChanged()
    {
        _activeInner?.Dispose();
        var repo = _registry.Active.Value;
        if (repo == null)
        {
            _activeInner = null;
            _active.Value = IndexOperations.Idle;
            return;
        }

        var moves = Get(repo.Id);
        // Becoming active surfaces the pending failure as the error dialog and clears the badge.
        if (moves.State.Value.PendingError is { } pending)
        {
            moves.SetPendingError(null);
            _bus.Broadcast(new ShowOperationErrorMessage(pending.Title, pending.Message));
        }
        _activeInner = moves.State.Subscribe(v => _active.Value = v);
    }

    public void Dispose()
    {
        _disposed = true;
        _activeSub?.Dispose();
        _activeInner?.Dispose();
        _active.Dispose();
        foreach (var moves in _repos.Values) moves.Dispose();
        _repos.Clear();
    }

    // A chunk whose git op succeeded and that no snapshot has reflected yet — a chunk exists in its
    // request's list only in that window. SnapshotsSeen counts the snapshots applied since, for the
    // settle fallback.
    private sealed class IndexChunk(IReadOnlyList<string> paths)
    {
        public IReadOnlyList<string> Paths { get; } = paths;
        public int SnapshotsSeen;
    }

    // One user action's worth of paths, in the order they were asked for. Every path counts as
    // pending from the moment the request starts; a path leaves the pending set when the chunk it
    // ran in settles.
    private sealed class IndexRequest(
        IReadOnlyList<string> paths,
        DiffSide toSide,
        MutationEffects effects,
        Func<Strings, string> failureTitle)
    {
        private readonly HashSet<string> _requested = new(paths, StringComparer.Ordinal);
        private readonly HashSet<string> _landed = new(StringComparer.Ordinal);
        private int _issued;

        public IReadOnlyList<string> Paths { get; } = paths;
        public DiffSide ToSide { get; } = toSide;
        public MutationEffects Effects { get; } = effects;
        public Func<Strings, string> FailureTitle { get; } = failureTitle;
        public List<IndexChunk> Chunks { get; } = new();

        public bool HasLanded(string path) => _landed.Contains(path);

        public void Finish(IndexChunk chunk)
        {
            Chunks.Add(chunk);
            _issued += chunk.Paths.Count;
        }

        public void Land(IndexChunk chunk)
        {
            Chunks.Remove(chunk);
            foreach (var path in chunk.Paths) _landed.Add(path);
        }

        public bool IsComplete => _issued == Paths.Count && Chunks.Count == 0;

        public LandedMove Landed()
        {
            var landed = new List<string>(_landed.Count);
            foreach (var path in Paths)
                if (_landed.Contains(path)) landed.Add(path);
            return new LandedMove(landed, ToSide, _requested);
        }
    }

    // One repo's requests, the published union of their unlanded paths, and the chain they run on.
    private sealed class RepoMoves : IDisposable
    {
        private readonly List<IndexRequest> _requests = new();
        private long? _reloadRequestedAt;

        public State<IndexOperations> State { get; } = new(IndexOperations.Idle);
        public Task Chain { get; set; } = Task.CompletedTask;

        public void Add(IndexRequest request)
        {
            _requests.Add(request);
            Publish();
        }

        public bool Contains(IndexRequest request) => _requests.Contains(request);

        public bool Remove(IndexRequest request)
        {
            if (!_requests.Remove(request)) return false;
            Publish();
            return true;
        }

        public void SetPendingError(PendingOperationError? error)
            => State.Value = State.Value with { PendingError = error };

        public void MarkReloadRequested() => _reloadRequestedAt = Stopwatch.GetTimestamp();

        public bool IsReloadOutstanding()
            => _reloadRequestedAt is { } at && Stopwatch.GetElapsedTime(at) < ReloadTimeout;

        public IReadOnlyList<string> ExcludePending(IReadOnlyList<string> paths)
        {
            var pending = State.Value.PendingPaths;
            if (pending.Count == 0) return paths;
            var kept = new List<string>(paths.Count);
            foreach (var p in paths)
                if (!pending.Contains(p)) kept.Add(p);
            return kept;
        }

        // Lands every finished chunk the snapshot reflects and returns the request of the most
        // recent of them. Any snapshot counts as the reload an intermediate chunk was waiting on.
        public LandedMove? Settle(IReadOnlyList<FileChange> unstaged, IReadOnlyList<FileChange> staged)
        {
            _reloadRequestedAt = null;
            if (_requests.Count == 0) return null;

            HashSet<string>? unstagedPaths = null;
            HashSet<string>? stagedPaths = null;
            IndexRequest? landed = null;
            for (var r = _requests.Count - 1; r >= 0; r--)
            {
                var request = _requests[r];
                for (var c = request.Chunks.Count - 1; c >= 0; c--)
                {
                    var chunk = request.Chunks[c];
                    chunk.SnapshotsSeen++;
                    unstagedPaths ??= PathsOf(unstaged);
                    stagedPaths ??= PathsOf(staged);
                    var (to, from) = request.ToSide == DiffSide.Staged
                        ? (stagedPaths, unstagedPaths)
                        : (unstagedPaths, stagedPaths);
                    if (!Reflects(chunk, to, from) && chunk.SnapshotsSeen < SettleFallbackSnapshots) continue;
                    landed ??= request;
                    request.Land(chunk);
                }
                if (request.IsComplete) _requests.RemoveAt(r);
            }
            if (landed == null) return null;
            Publish();
            return landed.Landed();
        }

        private static bool Reflects(IndexChunk chunk, HashSet<string> to, HashSet<string> from)
        {
            foreach (var path in chunk.Paths)
                if (!to.Contains(path) && from.Contains(path)) return false;
            return true;
        }

        private static HashSet<string> PathsOf(IReadOnlyList<FileChange> files)
        {
            var set = new HashSet<string>(files.Count, StringComparer.Ordinal);
            foreach (var f in files) set.Add(f.Path);
            return set;
        }

        private void Publish()
        {
            if (_requests.Count == 0)
            {
                State.Value = State.Value with { PendingPaths = IndexOperations.Idle.PendingPaths };
                return;
            }
            var pending = new HashSet<string>(StringComparer.Ordinal);
            foreach (var request in _requests)
                foreach (var path in request.Paths)
                    if (!request.HasLanded(path)) pending.Add(path);
            State.Value = State.Value with { PendingPaths = pending };
        }

        public void Dispose() => State.Dispose();
    }
}
