using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// A failure the user hasn't seen yet — it happened while the repo wasn't active. Drives the
// RepoBar error badge; shown as a dialog and cleared when the repo becomes active.
public readonly record struct PendingOperationError(string Title, string Message);

// The active repo's in-flight remote-operation state plus any pending unseen error. Projected by
// ActionsToolbarViewModel (spinner + disabled state) and, per repo, by the RepoBar error badge.
// Distinct from RepoOperationState (a merge/rebase in progress) — this tracks the push/pull/fetch
// lifecycle.
public sealed record RepoOperations(
    bool IsPushing,
    bool IsPulling,
    bool IsFetching,
    PendingOperationError? PendingError)
{
    public static readonly RepoOperations Idle = new(false, false, false, null);
}

// Single source of truth for in-flight remote operations (push/pull/fetch), keyed by repo id so an
// op started on one repo keeps running — and stays correctly tracked — after the user switches
// away. Mirrors RepoSnapshotStore's shape: per-repo state, an "active" projection that swaps on
// repo switch, and a Start() that wires the dispatcher once the UI loop exists.
public interface IRepoOperationsStore
{
    // The active repo's operation state. Swaps instantly on repo switch.
    IReadable<RepoOperations> Active { get; }

    // True when this repo has a remote-op failure the user hasn't seen yet (it happened while the
    // repo wasn't active). Feeds the RepoBar error badge via IRepoStatusStore. Call inside a reactive
    // binding — the underlying per-repo state read is auto-tracked.
    bool HasUnseenError(Guid repoId);

    // True while a push/pull/fetch is in flight on this repo. Tracked.
    bool IsBusy(Guid repoId);

    void Push(Repo repo, bool force = false);
    void Pull(Repo repo, PullStrategy? strategy = null);
    void Fetch(Repo repo);
}

/// <summary>
/// Owns the push/pull/fetch lifecycle per repo. The work already ran off-thread before; what this
/// adds is keeping the *state* keyed by repo id instead of on the single toolbar view model, so a
/// push on repo A keeps spinning (and its result/error lands correctly) after the user switches to
/// repo B and commits there. Different repos run truly in parallel — only same-type ops on one repo
/// are serialized (the in-flight flag), matching git's own per-repo index lock.
///
/// Like <see cref="RepoSnapshotStore"/> it exposes the *active* repo's slice as one observable and
/// is wired in <see cref="Start"/> once the UI dispatcher exists.
/// </summary>
internal sealed class RepoOperationsStore : IRepoOperationsStore, IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    // Set in Start once the UI dispatcher exists. Null until then — ops are inert before startup.
    private IUiDispatcher? _dispatcher;
    private bool _disposed;

    // Per-repo source of truth, created lazily on first touch and kept for the app's lifetime (one
    // tiny State per repo the user has acted on). UI-thread only — no locking needed.
    private readonly Dictionary<Guid, State<RepoOperations>> _states = new();

    // Mirrors the active repo's state so view models project a single observable.
    private readonly State<RepoOperations> _active = new(RepoOperations.Idle);
    private IDisposable? _activeInner;
    private IDisposable? _activeSub;

    public IReadable<RepoOperations> Active => _active;

    public RepoOperationsStore(IRepoRegistry registry, IGitService git, IMessageBus bus, ILocalizationService loc)
    {
        _registry = registry;
        _git = git;
        _bus = bus;
        _loc = loc;
    }

    public void Start(IUiDispatcher dispatcher)
    {
        if (_dispatcher != null) return; // idempotent
        _dispatcher = dispatcher;
        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());
    }

    public bool HasUnseenError(Guid repoId) => Get(repoId).Value.PendingError != null;

    public bool IsBusy(Guid repoId)
    {
        var v = Get(repoId).Value;
        return v.IsPushing || v.IsPulling || v.IsFetching;
    }

    public void Push(Repo repo, bool force = false)
    {
        var s = Get(repo.Id);
        if (s.Value.IsPushing) return;
        s.Value = s.Value with { IsPushing = true, PendingError = null };
        Run(repo, _loc.Strings.Value.ReposErrorPushFailed, _loc.Strings.Value.ToastPushed,
            () => _git.Push(repo, force) is GitOutcome.Failed f ? (false, f.Message, false) : (true, null, false),
            st => st with { IsPushing = false },
            // A successful push of the current branch leaves nothing left to send.
            optimisticSync: new RemoteSyncOptimisticMessage(repo.Id, Ahead: 0, Behind: null));
    }

    public void Pull(Repo repo, PullStrategy? strategy = null)
    {
        var s = Get(repo.Id);
        if (s.Value.IsPulling) return;
        s.Value = s.Value with { IsPulling = true, PendingError = null };
        var strings = _loc.Strings.Value;
        Run(repo, strings.ReposErrorPullFailed, strings.ToastPulled,
            () => _git.Pull(repo, strategy) switch
            {
                PullOutcome.Failed f => (false, f.Message, false),
                PullOutcome.Diverged => (false, strings.ReposErrorDivergentBranches, true),
                _ => (true, null, false),
            },
            st => st with { IsPulling = false },
            // A successful pull leaves the branch level with the upstream it pulled from.
            optimisticSync: new RemoteSyncOptimisticMessage(repo.Id, Ahead: null, Behind: 0));
    }

    public void Fetch(Repo repo)
    {
        var s = Get(repo.Id);
        if (s.Value.IsFetching) return;
        s.Value = s.Value with { IsFetching = true, PendingError = null };
        Run(repo, _loc.Strings.Value.ReposErrorFetchFailed, _loc.Strings.Value.ToastFetched,
            () => _git.Fetch(repo) is GitOutcome.Failed f ? (false, f.Message, false) : (true, null, false),
            st => st with { IsFetching = false });
    }

    private State<RepoOperations> Get(Guid id)
    {
        if (!_states.TryGetValue(id, out var s))
        {
            s = new State<RepoOperations>(RepoOperations.Idle);
            _states[id] = s;
        }
        return s;
    }

    private void OnActiveChanged()
    {
        _activeInner?.Dispose();
        var repo = _registry.Active.Value;
        if (repo == null)
        {
            _activeInner = null;
            _active.Value = RepoOperations.Idle;
            return;
        }

        var s = Get(repo.Id);
        // Becoming active surfaces the pending failure as the error dialog and clears the badge.
        if (s.Value.PendingError is { } pending)
        {
            s.Value = s.Value with { PendingError = null };
            _bus.Broadcast(new ShowOperationErrorMessage(pending.Title, pending.Message));
        }
        _activeInner = s.Subscribe(v => _active.Value = v); // fires immediately with the current value
    }

    // Runs the git op off-thread and posts the result back keyed by the *captured* repo — so the
    // completion lands on that repo's state no matter which repo is active when it finishes. No
    // generation guard: the in-flight flag already serializes same-type ops, and a late result is
    // never "stale" because it's applied to its own repo, not to whatever happens to be active.
    private void Run(
        Repo repo,
        string failureTitle,
        string successMessage,
        Func<(bool Success, string? Error, bool Diverged)> work,
        Func<RepoOperations, RepoOperations> clearInFlight,
        RemoteSyncOptimisticMessage? optimisticSync = null)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null) return;
        Task.Run(() =>
        {
            bool success = false;
            string? error = null;
            bool diverged = false;
            try { (success, error, diverged) = work(); }
            catch (Exception ex) { error = ex.Message; }
            dispatcher.Post(() => Complete(repo, failureTitle, successMessage, clearInFlight, optimisticSync, success, error, diverged));
        });
    }

    private void Complete(
        Repo repo,
        string failureTitle,
        string successMessage,
        Func<RepoOperations, RepoOperations> clearInFlight,
        RemoteSyncOptimisticMessage? optimisticSync,
        bool success,
        string? error,
        bool diverged)
    {
        if (_disposed) return;
        var s = Get(repo.Id);
        var next = clearInFlight(s.Value);

        if (success)
        {
            s.Value = next with { PendingError = null };
            _bus.Broadcast(new RefsChangedMessage(repo.Id));
            // Snap the ahead/behind number to its known outcome before the probe (kicked above)
            // reconciles, so it doesn't trail the toast by a beat.
            if (optimisticSync is { } sync) _bus.Broadcast(sync);
            _bus.Broadcast(new ShowToastMessage(ToastIntent.Success(successMessage)));
            return;
        }

        // A diverged pull on the repo you're looking at is recoverable in-app: hand it to the view
        // model to open the reconcile dialog. For a background repo there's nothing to interact with,
        // so it falls through to the badge path and the user re-pulls when they switch to it.
        if (diverged && _registry.Active.Value?.Id == repo.Id)
        {
            s.Value = next;
            _bus.Broadcast(new PullDivergedMessage(repo));
            return;
        }

        // Surface the failure: the error dialog if the repo is active, otherwise as the
        // unseen-error badge until the user switches to it.
        if (_registry.Active.Value?.Id == repo.Id)
        {
            s.Value = next;
            _bus.Broadcast(new ShowOperationErrorMessage(failureTitle, error ?? _loc.Strings.Value.CommonUnknownError));
        }
        else
        {
            s.Value = next with { PendingError = new PendingOperationError(failureTitle, error ?? _loc.Strings.Value.CommonUnknownError) };
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _activeSub?.Dispose();
        _activeInner?.Dispose();
        _active.Dispose();
        foreach (var s in _states.Values) s.Dispose();
        _states.Clear();
    }
}
