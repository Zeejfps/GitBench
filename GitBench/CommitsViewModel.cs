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
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;

    public IReadable<CommitsRenderState> Render { get; }
    public IReadable<string?> SelectedSha { get; }

    // Holds the most recent *successfully* loaded snapshot for the active repo, or null if we
    // have no good data for it (no repo, in-flight first load, or last load errored).
    // SHA-existence checks rely on this invariant — never assign an error snapshot here.
    private CommitSnapshot? _snapshot;
    // Repo whose history is currently reflected in state; used to clear selection on switch.
    private Guid? _renderedRepoId;
    private bool _isCheckingOutCommit;
    private bool _isMovingBranch;

    // Lane for the reset probe/apply op. Serialized by _isCheckingOutCommit; kept off the
    // default Gen lane so a repo-switch reload never drops an in-flight reset's result.
    private readonly GenerationGuard _resetGen;

    // Same idea for the detached-HEAD "reset branch to here" probe/move, on its own lane.
    private readonly GenerationGuard _moveGen;

    public CommitsViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IRepoSnapshotStore store)
        : base(dispatcher, CommitsState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;

        _resetGen = CreateLane();
        _moveGen = CreateLane();

        Render = Slice(s => s.Render);
        SelectedSha = Slice(s => s.SelectedSha);

        // History is projected from the store (which owns loading + caching + the soft refresh).
        Subscriptions.Add(store.Commits.Subscribe(OnStoreCommits));
        Subscriptions.Add(_bus.SubscribeScoped<CommitSelectedMessage>(OnCommitSelected));
    }

    // ---- selection ----

    public void SelectCommit(string sha)
    {
        if (_snapshot == null) return;
        if (State.Value.SelectedSha == sha) return;
        Update(s => s with { SelectedSha = sha });
        _bus.Broadcast(new CommitSelectedMessage(_snapshot.RepoId, sha));
    }

    // Up/Down arrow navigation through the loaded commit list. Moves the single selection
    // by <paramref name="delta"/> rows, clamped to the snapshot bounds. With nothing
    // selected yet, a Down lands on the first commit and an Up on the last so the cursor
    // has a visible start. Mirrors CommitDetailsViewModel.MoveSelection; the view scrolls
    // the new selection into view via its SelectedSha subscription.
    public void MoveSelection(int delta)
    {
        var snap = _snapshot;
        if (snap == null || snap.Commits.Count == 0) return;

        var current = State.Value.SelectedSha;
        var index = current == null ? -1 : IndexOfSha(snap, current);
        var next = ListNavigation.NextIndex(snap.Commits.Count, index, delta);
        SelectCommit(snap.Commits[next].Sha);
    }

    private static int IndexOfSha(CommitSnapshot snap, string sha)
    {
        for (var i = 0; i < snap.Commits.Count; i++)
        {
            if (snap.Commits[i].Sha == sha) return i;
        }
        return -1;
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

    // ---- create tag ----

    // Opens the CreateTagDialog targeting the given commit. No probe needed: tag creation
    // never touches the working tree, so we hand the dialog the commit's short SHA and
    // summary (looked up from the current snapshot) and let it run the git op itself.
    public void RequestCreateTag(string sha)
    {
        var snap = _snapshot;
        if (snap == null) return;
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != snap.RepoId) return;

        var shortSha = sha.Length >= 7 ? sha[..7] : sha;
        var summary = LookupSummary(snap, sha) ?? string.Empty;
        var capturedRepo = repo;
        var capturedSha = sha;
        _bus.Broadcast(new ShowDialogMessage(onClose => new CreateTagDialog(
            capturedRepo, capturedSha, shortSha, summary, onClose)));
    }

    // ---- create branch ----

    // Opens the CreateBranchDialog seeded with the given starting point (a commit SHA, or
    // "HEAD" for the detached-HEAD banner). The dialog defaults its "checkout after create"
    // box on, so the common flow captures the commits onto a branch and lands you on it.
    // Branch creation never touches the working tree, so no probe is needed.
    public void RequestCreateBranch(string startPoint)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var capturedRepo = repo;
        var capturedStart = startPoint;
        _bus.Broadcast(new ShowDialogMessage(onClose =>
            new CreateBranchDialog(capturedRepo, capturedStart, onClose)));
    }

    // ---- reset a branch to here (detached-HEAD recovery) ----

    // Detached-HEAD flow for "Reset <branch> to here": force-move the chosen branch to this
    // commit and check it out, bringing the detached commits onto a real branch. Probes
    // ancestry off-thread — a fast-forward (the branch tip is an ancestor of this commit) is
    // safe and applied immediately; otherwise the move would orphan the branch's unique
    // commits, so we open MoveBranchDialog to confirm first.
    public void RequestMoveBranch(string branchName, string sha)
    {
        if (_isMovingBranch) return;
        var snap = _snapshot;
        if (snap == null) return;
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != snap.RepoId) return;

        _isMovingBranch = true;
        var capturedRepo = repo;
        var capturedBranch = branchName;
        var capturedSha = sha;

        RunBackground<MoveBranchProbe>(
            work: () =>
            {
                if (_gitService.IsAncestor(capturedRepo, capturedBranch, capturedSha))
                {
                    var outcome = _gitService.MoveBranch(capturedRepo, capturedBranch, capturedSha, checkout: true);
                    return (new MoveBranchProbe.Moved(outcome.Success, outcome.ErrorMessage), null);
                }
                return (new MoveBranchProbe.NeedsConfirm(), null);
            },
            onResult: (probe, error) =>
            {
                _isMovingBranch = false;
                if (error != null)
                {
                    _bus.Broadcast(new ShowOperationErrorMessage("Reset branch failed", error));
                    return;
                }
                switch (probe)
                {
                    case MoveBranchProbe.Moved m when m.Success:
                        _bus.Broadcast(new RefsChangedMessage(capturedRepo.Id));
                        _bus.Broadcast(new WorkingTreeChangedMessage(capturedRepo.Id));
                        break;
                    case MoveBranchProbe.Moved m:
                        _bus.Broadcast(new ShowOperationErrorMessage(
                            "Reset branch failed", m.Error ?? "Reset branch failed."));
                        break;
                    case MoveBranchProbe.NeedsConfirm:
                        var shortSha = capturedSha.Length >= 7 ? capturedSha[..7] : capturedSha;
                        var summary = LookupSummary(snap, capturedSha) ?? string.Empty;
                        _bus.Broadcast(new ShowDialogMessage(onClose => new MoveBranchDialog(
                            capturedRepo, capturedBranch, capturedSha, shortSha, summary, onClose)));
                        break;
                }
            },
            lane: _moveGen);
    }

    // Outcome of the off-thread move probe, handed from work to onResult above.
    private abstract record MoveBranchProbe
    {
        public sealed record Moved(bool Success, string? Error) : MoveBranchProbe;
        public sealed record NeedsConfirm : MoveBranchProbe;
    }

    // ---- delete tag ----

    // Opens the DeleteTagDialog for the given tag. Like tag creation, deleting a tag never
    // touches the working tree, so no probe is needed — the dialog runs the git op itself.
    public void RequestDeleteTag(string tagName)
    {
        var snap = _snapshot;
        if (snap == null) return;
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != snap.RepoId) return;

        var capturedRepo = repo;
        var capturedTag = tagName;
        _bus.Broadcast(new ShowDialogMessage(onClose => new DeleteTagDialog(
            capturedRepo, capturedTag, onClose)));
    }

    // Outcome of the off-thread reset probe, handed from work to onResult above.
    private abstract record ResetProbe
    {
        public sealed record Failed(string Message) : ResetProbe;
        public sealed record CleanReset(bool Success, string? Error) : ResetProbe;
        public sealed record NeedsDialog(int Staged, int Unstaged) : ResetProbe;
    }

    // ---- loading ----

    // Projection of the store's commit slice. snap == null means "no data for the active repo
    // yet" (switching / cache miss) or no repo at all; a non-null snap is always for the active
    // repo (the store guards that). Renders Loading/NoRepo/Error/Loaded and keeps selection in
    // sync — cleared on a repo switch, pruned when the selected commit vanishes from a reload.
    private void OnStoreCommits(CommitSnapshot? snap)
    {
        var activeId = _registry.Active.Value?.Id;

        if (snap == null)
        {
            if (_renderedRepoId != activeId) ClearSelectionAndBroadcast(_renderedRepoId);
            _renderedRepoId = activeId;
            _snapshot = null;
            Update(s => s with
            {
                Render = activeId == null ? new CommitsRenderState.NoRepo() : new CommitsRenderState.Loading(),
            });
            return;
        }

        if (_renderedRepoId != snap.RepoId) ClearSelectionAndBroadcast(_renderedRepoId);
        _renderedRepoId = snap.RepoId;

        if (snap.ErrorMessage != null)
        {
            // Drop any prior good snapshot so the next reload shows "Loading…" not a stale graph.
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
                ClearSelectionAndBroadcast(snap.RepoId);
        }

        _bus.Broadcast(new CommitsLoadedMessage(snap.RepoId));
    }

    // Broadcasts against the given repo (the one the cleared selection belonged to), which is
    // what subscribers expect ("that repo's selection is now gone").
    private void ClearSelectionAndBroadcast(Guid? repoId)
    {
        if (State.Value.SelectedSha == null) return;
        Update(s => s with { SelectedSha = null });
        _bus.Broadcast(new CommitSelectedMessage(repoId ?? Guid.Empty, null));
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
