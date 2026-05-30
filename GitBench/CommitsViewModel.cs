using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

public abstract record CommitsRenderState
{
    public sealed record NoRepo : CommitsRenderState;
    public sealed record Loading : CommitsRenderState;
    public sealed record Error(string Message) : CommitsRenderState;
    public sealed record Loaded(CommitSnapshot Snapshot) : CommitsRenderState;
}

/// <summary>
/// View model for the commits history. Mirrors the BranchesViewModel pattern: state lives
/// in an immutable <see cref="CommitsState"/> record; the view subscribes to per-field
/// slices and calls command methods to drive interactions.
///
/// The load flow is generation-guarded through <see cref="ViewModelBase{TState}.RunBackground"/>
/// so stale loads (repo switched, newer reload) never clobber fresher state. The reset flow
/// runs as its own background op gated by <see cref="_isCheckingOutCommit"/> — it must always
/// apply its result (clearing the flag), so it deliberately bypasses the load generation.
/// </summary>
internal sealed class CommitsViewModel : ViewModelBase<CommitsState>
{
    private const int MaxCommits = 3000;

    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;

    public IReadable<CommitsRenderState> Render { get; }
    public IReadable<string?> SelectedSha { get; }

    // Holds the most recent *successfully* loaded snapshot, or null if we have no good
    // data for the active repo (no repo, in-flight first load, or last load errored).
    // Soft-refresh and SHA-existence checks both rely on this invariant — never assign
    // an error snapshot here.
    private CommitSnapshot? _snapshot;
    private Guid _loadingRepoId;
    private bool _isCheckingOutCommit;

    // Lane for the reset probe/apply op. Serialized by _isCheckingOutCommit; kept off the
    // default Gen lane so a repo-switch reload never drops an in-flight reset's result.
    private readonly GenerationGuard _resetGen;

    public CommitsViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
        : base(dispatcher, CommitsState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;

        _resetGen = CreateLane();

        Render = Slice(s => s.Render);
        SelectedSha = Slice(s => s.SelectedSha);

        Subscriptions.Add(_registry.Active.Subscribe(_ => StartLoadForActiveRepo()));
        Subscriptions.Add(_bus.SubscribeScoped<CommitCreatedMessage>(m => ReloadIfActiveRepo(m.RepoId)));
        Subscriptions.Add(_bus.SubscribeScoped<CommitSelectedMessage>(OnCommitSelected));
        Subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(m => ReloadIfActiveRepo(m.RepoId)));
    }

    // ---- selection ----

    public void SelectCommit(string sha)
    {
        if (_snapshot == null) return;
        if (State.Value.SelectedSha == sha) return;
        Update(s => s with { SelectedSha = sha });
        _bus.Broadcast(new CommitSelectedMessage(_snapshot.RepoId, sha));
    }

    // External selection requests (e.g. BranchesView tip clicks). Self-broadcasts come
    // back through here too; we dedupe against the current selection so the round trip is
    // harmless.
    private void OnCommitSelected(CommitSelectedMessage msg)
    {
        if (_snapshot == null || _snapshot.RepoId != msg.RepoId) return;
        if (State.Value.SelectedSha == msg.Sha) return;
        Update(s => s with { SelectedSha = msg.Sha });
    }

    // ---- reset to commit ----

    // Smart flow for "Reset <branch> to here":
    //   - Probe the working tree off-thread.
    //   - Clean tree → reset --hard immediately (no prompt; nothing local to lose).
    //   - Any staged/unstaged change → open ResetCommitDialog so the user picks
    //     soft/mixed/hard explicitly (each preserves a different slice of the dirty state).
    public void RequestReset(string sha)
    {
        if (_isCheckingOutCommit) return;
        var snap = _snapshot;
        if (snap == null) return;
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != snap.RepoId) return;

        _isCheckingOutCommit = true;
        var capturedRepo = repo;
        var capturedSha = sha;

        // All git I/O happens in work; onResult only dispatches UI. The probe decides between
        // an immediate hard reset (clean tree) and prompting for the reset mode (dirty tree).
        RunBackground<ResetProbe>(
            work: () =>
            {
                var changes = _gitService.GetLocalChanges(capturedRepo);
                if (changes.ErrorMessage != null)
                    return (new ResetProbe.Failed(changes.ErrorMessage), null);

                var staged = changes.Staged.Count;
                var unstaged = changes.Unstaged.Count;
                if (staged == 0 && unstaged == 0)
                {
                    var outcome = _gitService.ResetCurrent(capturedRepo, capturedSha, ResetMode.Hard);
                    return (new ResetProbe.CleanReset(outcome.Success, outcome.ErrorMessage), null);
                }
                return (new ResetProbe.NeedsDialog(staged, unstaged), null);
            },
            onResult: (probe, error) =>
            {
                _isCheckingOutCommit = false;
                if (error != null)
                {
                    _bus.Broadcast(new ShowOperationErrorMessage("Reset failed", error));
                    return;
                }
                switch (probe)
                {
                    case ResetProbe.Failed f:
                        _bus.Broadcast(new ShowOperationErrorMessage("Reset failed", f.Message));
                        break;
                    case ResetProbe.CleanReset c when c.Success:
                        _bus.Broadcast(new RefsChangedMessage(capturedRepo.Id));
                        _bus.Broadcast(new WorkingTreeChangedMessage(capturedRepo.Id));
                        break;
                    case ResetProbe.CleanReset c:
                        _bus.Broadcast(new ShowOperationErrorMessage(
                            "Reset failed", c.Error ?? "Reset failed."));
                        break;
                    case ResetProbe.NeedsDialog d:
                        var shortSha = capturedSha.Length >= 7 ? capturedSha[..7] : capturedSha;
                        var summary = LookupSummary(snap, capturedSha) ?? string.Empty;
                        _bus.Broadcast(new ShowDialogMessage(onClose => new ResetCommitDialog(
                            capturedRepo, capturedSha, shortSha, summary, snap.HeadBranchName,
                            d.Staged, d.Unstaged, onClose)));
                        break;
                }
            },
            lane: _resetGen);
    }

    // Outcome of the off-thread reset probe, handed from work to onResult above.
    private abstract record ResetProbe
    {
        public sealed record Failed(string Message) : ResetProbe;
        public sealed record CleanReset(bool Success, string? Error) : ResetProbe;
        public sealed record NeedsDialog(int Staged, int Unstaged) : ResetProbe;
    }

    // ---- loading ----

    private void ReloadIfActiveRepo(Guid repoId)
    {
        var active = _registry.Active.Value;
        if (active == null || active.Id != repoId) return;
        StartLoadForActiveRepo();
    }

    private void StartLoadForActiveRepo()
    {
        var active = _registry.Active.Value;

        if (active == null)
        {
            Gen.Bump();
            _snapshot = null;
            ClearSelectionAndBroadcast();
            Update(s => s with { Render = new CommitsRenderState.NoRepo() });
            return;
        }

        // Soft refresh: when we already have a snapshot for this repo (e.g. after a tab
        // round-trip, or after a commit/push), keep it visible while a fresh one loads in
        // the background. Avoids a "Loading…" flash and preserves scroll/selection.
        var isSoftRefresh = _snapshot != null && _snapshot.RepoId == active.Id;
        if (!isSoftRefresh)
        {
            _snapshot = null;
            ClearSelectionAndBroadcast();
            Update(s => s with { Render = new CommitsRenderState.Loading() });
        }
        _loadingRepoId = active.Id;

        var repo = active;
        var service = _gitService;
        RunBackground<CommitSnapshot>(
            work: () => (service.Load(repo, MaxCommits), null),
            onResult: (snap, error) =>
            {
                // RunBackground reports a thrown exception via the separate `error`
                // channel; ApplyLoadedSnapshot's error path keys off the snapshot's own
                // ErrorMessage, so wrap it back into a synthetic snapshot.
                var applied = snap ?? new CommitSnapshot(
                    repo.Id, repo.Path, Array.Empty<CommitNode>(), 0, false,
                    error ?? "Failed to load history.");
                ApplyLoadedSnapshot(applied);
            });
    }

    private void ApplyLoadedSnapshot(CommitSnapshot snap)
    {
        if (snap.RepoId != _loadingRepoId) return;

        if (snap.ErrorMessage != null)
        {
            // Drop any prior successful snapshot so the next reload shows "Loading…"
            // rather than silently soft-refreshing on top of an Error placeholder.
            _snapshot = null;
            Update(s => s with { Render = new CommitsRenderState.Error(snap.ErrorMessage) });
        }
        else
        {
            _snapshot = snap;
            Update(s => s with { Render = new CommitsRenderState.Loaded(snap) });
            // Selection survives only if the commit still exists in the new snapshot
            // (e.g. it may have been pruned by a rebase or reset).
            var selected = State.Value.SelectedSha;
            if (selected != null && !SnapshotContainsSha(snap, selected))
                ClearSelectionAndBroadcast();
        }

        _bus.Broadcast(new CommitsLoadedMessage(snap.RepoId));
    }

    // Broadcasts against _loadingRepoId — that's the *previous* repo at the moment we
    // clear, which is what subscribers expect ("the prev repo's selection is now gone").
    private void ClearSelectionAndBroadcast()
    {
        if (State.Value.SelectedSha == null) return;
        Update(s => s with { SelectedSha = null });
        _bus.Broadcast(new CommitSelectedMessage(_loadingRepoId, null));
    }

    private static bool SnapshotContainsSha(CommitSnapshot snap, string sha)
    {
        for (var i = 0; i < snap.Commits.Count; i++)
        {
            if (snap.Commits[i].Sha == sha) return true;
        }
        return false;
    }

    private static string? LookupSummary(CommitSnapshot snap, string sha)
    {
        for (var i = 0; i < snap.Commits.Count; i++)
        {
            if (snap.Commits[i].Sha == sha) return snap.Commits[i].Summary;
        }
        return null;
    }
}

internal sealed record CommitsState(
    CommitsRenderState Render,
    string? SelectedSha)
{
    public static CommitsState Initial { get; } = new(new CommitsRenderState.NoRepo(), null);
}
