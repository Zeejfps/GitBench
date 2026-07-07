using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Branches;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Commits;

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
/// so stale loads (repo switched, newer reload) never clobber fresher state. The reset, move,
/// and apply flows each run exclusively on their own lane via TryRunBackground/TryRunOutcome,
/// kept off the load generation so a repo-switch reload never drops their results.
/// </summary>
internal sealed class CommitsViewModel : ViewModelBase<CommitsState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly PreferencesService _preferences;

    public IReadable<CommitsRenderState> Render { get; }
    public IReadable<string?> SelectedSha { get; }
    // True while a non-empty search query is filtering the list; drives the view to drop the
    // graph column (lanes are meaningless across a subset).
    public IReadable<bool> IsFiltering { get; }
    // True while the graph hides commits that exist only on remote branches with no local
    // counterpart. Persisted as a preference; drives the search bar's filter toggle tint.
    public IReadable<bool> HideRemoteOnly { get; }

    // Holds the most recent *successfully* loaded snapshot for the active repo, or null if we
    // have no good data for it (no repo, in-flight first load, or last load errored).
    // SHA-existence checks rely on this invariant — never assign an error snapshot here.
    private CommitSnapshot? _snapshot;
    // The snapshot currently shown — the filtered subset when a query is active, else the full
    // snapshot. List navigation indexes into this; _snapshot stays the full set for selection
    // pruning and per-commit lookups.
    private CommitSnapshot? _rendered;
    // Repo whose history is currently reflected in state; used to clear selection on switch.
    private Guid? _renderedRepoId;

    // Caches the remote-filtered projection of _snapshot so search keystrokes don't re-run lane
    // assignment; keyed by snapshot instance, invalidated when the store emits a new one.
    private CommitSnapshot? _remoteFilterSource;
    private CommitSnapshot? _remoteFilterResult;

    // Exclusive lane for the reset probe/apply op; kept off the default Gen lane so a
    // repo-switch reload never drops an in-flight reset's result.
    private readonly GenerationGuard _resetGen;

    // Same idea for the detached-HEAD "reset branch to here" probe/move, on its own lane.
    private readonly GenerationGuard _moveGen;

    // Exclusive lane for cherry-pick / revert applies.
    private readonly GenerationGuard _applyGen;

    public CommitsViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IRepoSnapshotStore store,
        ILocalizationService loc,
        PreferencesService preferences)
        : base(dispatcher, CommitsState.Initial with
        {
            HideRemoteOnly = preferences.Current.HideRemoteOnlyBranches,
        })
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _loc = loc;
        _preferences = preferences;

        _resetGen = CreateLane();
        _moveGen = CreateLane();
        _applyGen = CreateLane();

        Render = Slice(s => s.Render);
        SelectedSha = Slice(s => s.SelectedSha);
        IsFiltering = Slice(s => s.IsFiltering);
        HideRemoteOnly = Slice(s => s.HideRemoteOnly);

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
    // has a visible start. The view scrolls the new selection into view via its SelectedSha
    // subscription.
    public void MoveSelection(int delta)
    {
        // Navigate the rendered list so arrows step through visible (filtered) rows only.
        var snap = _rendered ?? _snapshot;
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
        var snap = _snapshot;
        if (snap == null) return;
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != snap.RepoId) return;

        // All git I/O happens in work; onResult only dispatches UI. The probe decides between an
        // immediate hard reset (clean tree) and prompting for the reset mode (dirty tree).
        TryRunBackground<ResetProbe>(
            _resetGen,
            work: () => ProbeReset(repo, sha),
            onResult: (probe, error) => OnResetProbed(probe, error, repo, sha, snap));
    }

    private (ResetProbe?, string?) ProbeReset(Repo repo, string sha)
    {
        var fetched = _gitService.GetLocalChanges(repo);
        if (fetched is Fetched<LocalChangesSnapshot>.Failed failed)
            return (new ResetProbe.Failed(failed.Message), null);

        var changes = ((Fetched<LocalChangesSnapshot>.Ok)fetched).Value;
        var staged = changes.Staged.Count;
        var unstaged = changes.Unstaged.Count;
        if (staged == 0 && unstaged == 0)
            return (new ResetProbe.CleanReset(_gitService.ResetCurrent(repo, sha, ResetMode.Hard)), null);
        return (new ResetProbe.NeedsDialog(staged, unstaged), null);
    }

    private void OnResetProbed(ResetProbe? probe, string? error, Repo repo, string sha, CommitSnapshot snap)
    {
        var strings = _loc.Strings.Value;
        if (error != null)
        {
            _bus.Broadcast(new ShowOperationErrorMessage(strings.CommitsErrorResetFailed, error));
            return;
        }
        switch (probe)
        {
            case ResetProbe.Failed f:
                _bus.Broadcast(new ShowOperationErrorMessage(strings.CommitsErrorResetFailed, f.Message));
                break;
            case ResetProbe.CleanReset { Outcome: GitOutcome.Failed failed }:
                _bus.Broadcast(new ShowOperationErrorMessage(strings.CommitsErrorResetFailed, failed.Message));
                break;
            case ResetProbe.CleanReset:
                _bus.Broadcast(new RefsChangedMessage(repo.Id));
                _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                break;
            case ResetProbe.NeedsDialog d:
                ShowResetDialog(repo, sha, snap, d);
                break;
        }
    }

    private void ShowResetDialog(Repo repo, string sha, CommitSnapshot snap, ResetProbe.NeedsDialog d)
    {
        var shortSha = sha.Length >= 7 ? sha[..7] : sha;
        var summary = LookupSummary(snap, sha) ?? string.Empty;
        _bus.Broadcast(new ShowDialogMessage(onClose => new ResetCommitDialog
        {
            Repo = repo,
            Sha = sha,
            ShortSha = shortSha,
            Summary = summary,
            BranchName = snap.HeadBranchName,
            StagedCount = d.Staged,
            UnstagedCount = d.Unstaged,
            OnClose = onClose,
        }));
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
        _bus.Broadcast(new ShowDialogMessage(onClose => new CreateTagDialog
        {
            Repo = capturedRepo,
            Sha = capturedSha,
            ShortSha = shortSha,
            Summary = summary,
            OnClose = onClose,
        }));
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
        _bus.Broadcast(new ShowDialogMessage(onClose => new CreateBranchDialog
        {
            Repo = capturedRepo,
            SuggestedStartPoint = capturedStart,
            OnClose = onClose,
        }));
    }

    // ---- reset a branch to here (detached-HEAD recovery) ----

    // Detached-HEAD flow for "Reset <branch> to here": force-move the chosen branch to this
    // commit and check it out, bringing the detached commits onto a real branch. Probes
    // ancestry off-thread — a fast-forward (the branch tip is an ancestor of this commit) is
    // safe and applied immediately; otherwise the move would orphan the branch's unique
    // commits, so we open MoveBranchDialog to confirm first.
    public void RequestMoveBranch(string branchName, string sha)
    {
        var snap = _snapshot;
        if (snap == null) return;
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != snap.RepoId) return;

        var capturedRepo = repo;
        var capturedBranch = branchName;
        var capturedSha = sha;

        TryRunBackground<MoveBranchProbe>(
            _moveGen,
            work: () =>
            {
                if (_gitService.IsAncestor(capturedRepo, capturedBranch, capturedSha))
                    return (new MoveBranchProbe.Moved(_gitService.MoveBranch(capturedRepo, capturedBranch, capturedSha, checkout: true)), null);
                return (new MoveBranchProbe.NeedsConfirm(), null);
            },
            onResult: (probe, error) =>
            {
                var strings = _loc.Strings.Value;
                if (error != null)
                {
                    _bus.Broadcast(new ShowOperationErrorMessage(strings.CommitsErrorResetBranchFailed, error));
                    return;
                }
                switch (probe)
                {
                    case MoveBranchProbe.Moved { Outcome: GitOutcome.Failed failed }:
                        _bus.Broadcast(new ShowOperationErrorMessage(strings.CommitsErrorResetBranchFailed, failed.Message));
                        break;
                    case MoveBranchProbe.Moved:
                        _bus.Broadcast(new RefsChangedMessage(capturedRepo.Id));
                        _bus.Broadcast(new WorkingTreeChangedMessage(capturedRepo.Id));
                        break;
                    case MoveBranchProbe.NeedsConfirm:
                        var shortSha = capturedSha.Length >= 7 ? capturedSha[..7] : capturedSha;
                        var summary = LookupSummary(snap, capturedSha) ?? string.Empty;
                        _bus.Broadcast(new ShowDialogMessage(onClose => new MoveBranchDialog
                        {
                            Repo = capturedRepo,
                            BranchName = capturedBranch,
                            Sha = capturedSha,
                            ShortSha = shortSha,
                            Summary = summary,
                            OnClose = onClose,
                        }));
                        break;
                }
            });
    }

    // Outcome of the off-thread move probe, handed from work to onResult above.
    private abstract record MoveBranchProbe
    {
        public sealed record Moved(GitOutcome Outcome) : MoveBranchProbe;
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
        _bus.Broadcast(new ShowDialogMessage(onClose => new DeleteTagDialog
        {
            Repo = capturedRepo,
            TagName = capturedTag,
            OnClose = onClose,
        }));
    }

    // ---- cherry-pick / revert ----

    // Replays the named commit onto the current branch as a new commit. No confirm dialog —
    // cherry-pick is non-destructive and a conflict is fully recoverable via the operation
    // banner's Abort. A clean apply refreshes refs + working tree; a conflicting apply does the
    // same and lets the banner (which detects CHERRY_PICK_HEAD) drive resolve/continue/abort; a
    // hard failure (dirty tree, bad ref, …) surfaces an error.
    public void RequestCherryPick(string sha) =>
        RunCommitApply(sha, _loc.Strings.Value.CommitsErrorCherryPickFailed, (repo, s) => _gitService.CherryPick(repo, s));

    // Creates a new commit that undoes the named commit. Same off-thread flow as cherry-pick;
    // its conflict sentinel is REVERT_HEAD, also handled by the operation banner.
    public void RequestRevert(string sha) =>
        RunCommitApply(sha, _loc.Strings.Value.CommitsErrorRevertFailed, (repo, s) => _gitService.RevertCommit(repo, s));

    // Shared driver for the cherry-pick / revert one-shot ops: gate re-entry, run the git op
    // off-thread on the apply lane, then either refresh (success, incl. Conflicted — the
    // operation banner takes over) or show an error.
    private void RunCommitApply(string sha, string failureTitle, Func<Repo, string, MergeLikeOutcome> op)
    {
        var snap = _snapshot;
        if (snap == null) return;
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != snap.RepoId) return;

        var capturedRepo = repo;
        var capturedSha = sha;

        TryRunOutcome(
            _applyGen,
            work: () => op(capturedRepo, capturedSha),
            onResult: outcome =>
            {
                if (outcome is MergeLikeOutcome.Failed failed)
                {
                    _bus.Broadcast(new ShowOperationErrorMessage(failureTitle, failed.Message));
                    return;
                }
                _bus.Broadcast(new RefsChangedMessage(capturedRepo.Id));
                _bus.Broadcast(new WorkingTreeChangedMessage(capturedRepo.Id));
            });
    }

    // Outcome of the off-thread reset probe, handed from work to onResult above.
    private abstract record ResetProbe
    {
        public sealed record Failed(string Message) : ResetProbe;
        public sealed record CleanReset(GitOutcome Outcome) : ResetProbe;
        public sealed record NeedsDialog(int Staged, int Unstaged) : ResetProbe;
    }

    // ---- search / filter ----

    // Live filter over the currently-loaded history. Matching is case-insensitive against the
    // commit summary, author, or SHA prefix. Filtering is purely a projection of the loaded
    // snapshot (no new git load) — so it's scoped to the capped window the store already holds;
    // the existing "History truncated" banner signals when that window is partial.
    public void SetSearchQuery(string? query)
    {
        var q = query ?? string.Empty;
        if (State.Value.Query == q) return;

        if (_snapshot == null)
        {
            // No history loaded yet — just remember the query so the next load applies it.
            _rendered = null;
            Update(s => s with { Query = q, IsFiltering = !string.IsNullOrWhiteSpace(q) });
            return;
        }

        ApplyProjection(_snapshot, q);
    }

    // Hides remote-only branches (commits reachable only from remotes with no local
    // counterpart) and persists the choice. Purely a projection of the loaded snapshot —
    // toggling never re-runs the git walk, only lane reassignment over the kept rows.
    public void ToggleRemoteOnlyFilter()
    {
        var hide = !State.Value.HideRemoteOnly;
        _preferences.SetHideRemoteOnlyBranches(hide);
        Update(s => s with { HideRemoteOnly = hide });
        if (_snapshot != null) ApplyProjection(_snapshot, State.Value.Query);
    }

    // Builds the Loaded render for snap under the given query, updates _rendered, and writes
    // Render/Query/IsFiltering in one state update. The remote filter applies first (graph
    // intact, lanes reassigned); an empty/whitespace query is then the unfiltered fast path.
    private void ApplyProjection(CommitSnapshot snap, string query)
    {
        var basis = State.Value.HideRemoteOnly ? FilterUnmatchedRemotes(snap) : snap;

        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            _rendered = basis;
            Update(s => s with
            {
                Render = new CommitsRenderState.Loaded(basis),
                Query = query,
                IsFiltering = false,
            });
            return;
        }

        var matched = new List<CommitNode>();
        foreach (var node in basis.Commits)
            if (MatchesQuery(node, trimmed)) matched.Add(node);

        // Lanes are meaningless across a filtered subset, so the view renders a flat list;
        // LaneCount = 0 collapses the graph column.
        var filtered = basis with { Commits = matched, LaneCount = 0 };
        _rendered = filtered;

        Update(s => s with
        {
            Render = new CommitsRenderState.Loaded(filtered),
            Query = query,
            IsFiltering = true,
        });
    }

    private CommitSnapshot FilterUnmatchedRemotes(CommitSnapshot snap)
    {
        if (!ReferenceEquals(_remoteFilterSource, snap))
        {
            _remoteFilterSource = snap;
            _remoteFilterResult = CommitHistoryFilter.ExcludeUnmatchedRemotes(snap);
        }
        return _remoteFilterResult!;
    }

    private static bool MatchesQuery(CommitNode node, string q) =>
        node.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)
        || node.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
        || node.Sha.StartsWith(q, StringComparison.OrdinalIgnoreCase);

    // ---- loading ----

    // Projection of the store's commit slice. snap == null means "no data for the active repo
    // yet" (switching / cache miss) or no repo at all; a non-null snap is always for the active
    // repo (the store guards that). Renders Loading/NoRepo/Error/Loaded and keeps selection in
    // sync — cleared on a repo switch, pruned when the selected commit vanishes from a reload.
    private void OnStoreCommits(Fetched<CommitSnapshot>? fetched)
    {
        var activeId = _registry.Active.Value?.Id;

        if (fetched == null)
        {
            if (_renderedRepoId != activeId) ClearSelectionAndBroadcast(_renderedRepoId);
            _renderedRepoId = activeId;
            _snapshot = null;
            _rendered = null;
            _remoteFilterSource = null;
            _remoteFilterResult = null;
            Update(s => s with
            {
                Render = activeId == null ? new CommitsRenderState.NoRepo() : new CommitsRenderState.Loading(),
            });
            return;
        }

        if (_renderedRepoId != activeId) ClearSelectionAndBroadcast(_renderedRepoId);
        _renderedRepoId = activeId;

        if (fetched is Fetched<CommitSnapshot>.Failed failed)
        {
            // Drop any prior good snapshot so the next reload shows "Loading…" not a stale graph.
            _snapshot = null;
            _rendered = null;
            _remoteFilterSource = null;
            _remoteFilterResult = null;
            Update(s => s with { Render = new CommitsRenderState.Error(failed.Message) });
        }
        else
        {
            var snap = ((Fetched<CommitSnapshot>.Ok)fetched).Value;
            _snapshot = snap;
            // Re-apply the active filter so a soft refresh / reload keeps the user's query.
            ApplyProjection(snap, State.Value.Query);
            // Selection survives only if the commit still exists in the new snapshot
            // (e.g. it may have been pruned by a rebase or reset).
            var selected = State.Value.SelectedSha;
            if (selected != null && !SnapshotContainsSha(snap, selected))
                ClearSelectionAndBroadcast(snap.RepoId);

            // With nothing selected (fresh load, repo switch, or the prior selection just
            // pruned), default to the commit HEAD points at so the details panel opens on
            // something instead of the empty "select a commit" placeholder.
            if (State.Value.SelectedSha == null)
            {
                var headSha = FindHeadSha(snap);
                if (headSha != null) SelectCommit(headSha);
            }
        }

        _bus.Broadcast(new CommitsLoadedMessage(activeId ?? Guid.Empty));
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

    // The commit HEAD points at: the tip of the checked-out branch (its IsCurrent badge) or,
    // when detached, the commit carrying the standalone HEAD badge. Null if HEAD isn't in the
    // loaded window — shouldn't happen, since the walk is always seeded from HEAD.
    private static string? FindHeadSha(CommitSnapshot snap)
    {
        foreach (var commit in snap.Commits)
        {
            foreach (var badge in commit.Refs)
            {
                if (badge.IsCurrent || badge.Kind == RefKind.Head) return commit.Sha;
            }
        }
        return null;
    }
}

internal sealed record CommitsState(
    CommitsRenderState Render,
    string? SelectedSha,
    string Query,
    bool IsFiltering,
    bool HideRemoteOnly)
{
    public static CommitsState Initial { get; } = new(new CommitsRenderState.NoRepo(), null, "", false, false);
}
