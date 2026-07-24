using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Operations;
using GitBench.Features.Repos;
using GitBench.Features.Stash;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// View model for the Branches sidebar. Mirrors the LocalChangesViewModel pattern:
/// state lives in an immutable <see cref="BranchesState"/> record; views subscribe to
/// per-field slices and call command methods to drive interactions and git ops.
///
/// The VM exposes the raw model — listing, UI open-state, selection, busy-branch — and
/// command methods keyed on semantic identifiers (branch full paths, remote names,
/// folder keys). It does not produce render rows: the view derives rows from
/// <see cref="Listing"/> and <see cref="Ui"/> and owns its own layout/copy choices.
///
/// Section/folder open-state is mirrored into <see cref="IRepoRegistry.SetBranchesUi"/>
/// after every toggle so it persists across repo switches.
///
/// A single <see cref="BranchesState.IsBranchOpInFlight"/> flag serializes checkout /
/// rename / delete from the UI's perspective; the per-repo GitService lock serializes at
/// the actual command level. Stash apply has its own flag because it's a different
/// concept the user wouldn't expect to be blocked by a branch op.
/// </summary>
internal sealed class BranchesViewModel : ViewModelBase<BranchesState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly State<MainViewMode> _mode;
    private readonly ILocalizationService _loc;

    public IReadable<BranchListing?> Listing { get; }
    public IReadable<BranchesUiState> Ui { get; }
    public IReadable<BranchSelection?> Selection { get; }
    public IReadable<string?> BusyBranch { get; }
    public IReadable<string?> PendingHead { get; }
    public IReadable<string?> LoadError { get; }
    public IReadable<bool> IsLoading { get; }
    public IReadable<IReadOnlySet<string>> WorktreeBranches { get; }

    // The flattened, collapse-aware row list the sidebar renders, projected with value-equality
    // reconciliation so a reload only remounts the rows whose data actually changed and a collapse
    // only mounts/unmounts the affected subtree.
    public ObservableList<BranchRow> Rows => _rows.Items;

    // The error message shown in place of the list (null unless a load failed); paired with
    // ContentKind, which selects list vs. loading skeleton vs. this message.
    public IReadable<string?> PlaceholderText => _placeholderText;
    public IReadable<BranchesContentKind> ContentKind => _contentKind;

    private Guid _activeRepoId;

    // Op lanes for the mutating commands. Checkout and fast-forward share one lane because
    // BusyBranch already serializes them; stash apply runs exclusively on its own lane (it's
    // a distinct concept from branch ops — don't fold it into IsBranchOpInFlight). They are
    // deliberately separate from the default Gen lane, which is only bumped by loads/repo-
    // switch — a mutation must still report its result after a switch.
    private readonly GenerationGuard _branchOpGen;
    private readonly GenerationGuard _stashGen;

    private readonly Derived<IReadOnlyList<BranchRow>> _rowModels;
    private readonly KeyedViewModelList<BranchRow, BranchRow, BranchRow> _rows;
    private readonly Derived<string?> _placeholderText;
    private readonly Derived<BranchesContentKind> _contentKind;

    public BranchesViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        State<MainViewMode> mode,
        IRepoSnapshotStore store,
        IRepoStatusStore status,
        ILocalizationService loc)
        : base(dispatcher, BranchesState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _mode = mode;
        _loc = loc;

        _branchOpGen = CreateLane();
        _stashGen = CreateLane();

        Listing = Slice(s => s.Listing);
        Ui = Slice(s => s.Ui);
        Selection = Slice(s => s.Selection);
        BusyBranch = Slice(s => s.BusyBranch);
        PendingHead = Slice(s => s.PendingHead);
        LoadError = Slice(s => s.LoadError);
        IsLoading = Slice(s => s.IsLoading);
        WorktreeBranches = Slice(s => s.WorktreeBranches);

        // Reading the status store here — rather than carrying HEAD's counts in the listing — is what
        // keeps the badge and the toolbar's push/pull enablement moving in the same tick.
        _rowModels = new Derived<IReadOnlyList<BranchRow>>(
            () => BranchTreeBuilder.BuildRows(Listing.Value, Ui.Value, status.Active.Value));
        _rows = new KeyedViewModelList<BranchRow, BranchRow, BranchRow>(_rowModels, r => r, r => r);
        _placeholderText = new Derived<string?>(() =>
            LoadError.Value is { } err ? _loc.Strings.Value.BranchesLoadError(err) : null);
        // Loading shows the skeleton (no text); a failure shows the message; otherwise — listed, or no
        // repo yet — the tree (empty when there's nothing to list), matching the prior behavior.
        _contentKind = new Derived<BranchesContentKind>(() =>
            LoadError.Value != null ? BranchesContentKind.Message
            : Listing.Value == null && IsLoading.Value ? BranchesContentKind.Loading
            : BranchesContentKind.List);

        // The listing is projected from the store (which owns loading + caching). OnActiveRepoChanged
        // handles only the per-repo UI bits (fold state, worktree set, selection reset).
        Subscriptions.Add(_registry.Active.Subscribe(_ => OnActiveRepoChanged()));
        Subscriptions.Add(store.Branches.Subscribe(OnStoreBranches));
        Subscriptions.Add(_bus.SubscribeScoped<CommitSelectedMessage>(OnCommitSelected));
        Subscriptions.Add(_bus.SubscribeScoped<CheckoutRequestedMessage>(OnCheckoutRequested));
        Subscriptions.Add(_bus.SubscribeScoped<WorktreesChangedMessage>(OnWorktreesChanged));
        Subscriptions.Add(_registry.WorktreesChanged.Subscribe(_ => RefreshWorktreeBranches()));
    }

    private void OnWorktreesChanged(WorktreesChangedMessage _) => RefreshWorktreeBranches();

    // Checkout requests raised elsewhere (history badge clicks) funnel into the same activation
    // paths as sidebar double-clicks, so every guard applies identically.
    private void OnCheckoutRequested(CheckoutRequestedMessage msg)
    {
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != msg.RepoId) return;
        if (msg.RemoteName != null)
            ActivateRemoteBranch(msg.RemoteName, msg.BranchName);
        else
            ActivateLocalBranch(msg.BranchName, isHead: false);
    }

    // Set of local-branch names that are checked out somewhere other than the active row.
    // Used by BranchesView to annotate those branches so the user knows trying to check
    // them out here would conflict. Built from sibling worktrees of the active primary
    // (or, when a worktree is active, from the primary and all other siblings).
    //
    // Submodules are excluded: they have their own .git directory with independent refs,
    // so they don't compete for branch checkouts with the parent or with sibling worktrees.
    private void RefreshWorktreeBranches()
    {
        var active = _registry.Active.Value;
        if (active is null || active.IsSubmodule)
        {
            Update(s => s.WorktreeBranches.Count == 0 ? s : s with { WorktreeBranches = EmptyStringSet });
            return;
        }
        var primaryId = active.ParentRepoId ?? active.Id;

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in _registry.Repos)
        {
            if (r.Id == active.Id) continue;
            if (r.IsSubmodule) continue;
            var rootId = r.ParentRepoId ?? r.Id;
            if (rootId != primaryId) continue;
            // Repo.Branch is populated from `git worktree list` by WorktreeSyncService for
            // both the primary and its worktrees. Detached HEADs leave it null and produce
            // no marker (correct: there's no branch name to take).
            if (!string.IsNullOrEmpty(r.Branch))
                set.Add(r.Branch);
        }
        Update(s => s with { WorktreeBranches = set });
    }

    private static readonly IReadOnlySet<string> EmptyStringSet = new HashSet<string>();


    private void OnActiveRepoChanged()
    {
        var active = _registry.Active.Value;
        _activeRepoId = active?.Id ?? Guid.Empty;

        if (active == null)
        {
            Gen.Bump();
            Update(_ => BranchesState.Initial);
            return;
        }

        var ui = _registry.GetBranchesUi(active.Id);
        // Listing/IsLoading/LoadError are driven by the store subscription (OnStoreBranches);
        // here we only refresh the per-repo UI fold state, drop the previous repo's selection,
        // and recompute the worktree-branch set. Listing is deliberately left untouched.
        Update(s => s with { Ui = ui, Selection = null, PendingHead = null });
        RefreshWorktreeBranches();
    }

    // Projection of the store's branch slice. null means no data for the active repo yet
    // (switching / cache miss) → show "Loading…"; a non-null listing is applied as before.
    private void OnStoreBranches(Fetched<BranchListing>? fetched)
    {
        switch (fetched)
        {
            case null:
                if (_registry.Active.Value == null) return; // no repo → OnActiveRepoChanged sets Initial
                Update(s => s with { Listing = null, IsLoading = true, LoadError = null });
                return;
            case Fetched<BranchListing>.Failed failed:
            {
                var hadSelection = State.Value.Selection != null;
                Update(s => s with { Listing = null, IsLoading = false, LoadError = failed.Message, Selection = null });
                if (hadSelection)
                    _bus.Broadcast(new CommitSelectedMessage(_activeRepoId, null));
                return;
            }
            case Fetched<BranchListing>.Ok ok:
                ApplyListing(ok.Value);
                return;
        }
    }

    private void OnCommitSelected(CommitSelectedMessage msg)
    {
        if (msg.RepoId != _activeRepoId) return;
        var current = State.Value.Selection;
        if (current == null) return;
        if (msg.Sha == current.Value.TipSha) return;
        Update(s => s with { Selection = null });
    }

    private void ApplyListing(BranchListing listing)
    {
        var hadSelection = State.Value.Selection != null;
        Update(s =>
        {
            // Drop selection if it points at a ref that no longer exists (covers
            // post-delete / post-rename cleanup uniformly — the dialog presenters just
            // broadcast RefsChangedMessage and rely on this check).
            var selection = s.Selection;
            if (selection.HasValue && !RefStillExists(selection.Value, listing))
                selection = null;

            var pendingHead = s.PendingHead;
            if (pendingHead != null && ListingHeadIs(listing, pendingHead))
                pendingHead = null;

            return s with
            {
                Listing = listing,
                LoadError = null,
                IsLoading = false,
                Selection = selection,
                PendingHead = pendingHead,
            };
        });

        // Tell the commits panel to drop its tip highlight only when a branch selection we were
        // holding just disappeared (its ref vanished in this reload). Broadcasting whenever no
        // branch is selected would also wipe an independent commit selection on every refresh —
        // e.g. tagging or fetching reloads the branch listing while a commit is selected.
        if (hadSelection && State.Value.Selection == null)
            _bus.Broadcast(new CommitSelectedMessage(listing.RepoId, null));
    }

    private static bool RefStillExists(BranchSelection sel, BranchListing listing)
    {
        if (sel.IsStash)
        {
            foreach (var s in listing.Stashes)
                if ($"stash@{{{s.Index}}}" == sel.FullPath) return true;
            return false;
        }
        if (sel.IsRemote)
        {
            foreach (var rg in listing.Remotes)
            {
                if (rg.Name != sel.RemoteName) continue;
                foreach (var b in rg.Branches)
                    if (b.Name == sel.FullPath) return true;
            }
            return false;
        }
        foreach (var b in listing.LocalBranches)
            if (b.Name == sel.FullPath) return true;
        return false;
    }

    private static bool ListingHeadIs(BranchListing listing, string branchName)
    {
        foreach (var b in listing.LocalBranches)
            if (b is LocalBranchEntry.Head) return b.Name == branchName;
        return false;
    }

    // ---- section/folder toggles ----

    public void ToggleLocalSection() => MutateUi(ui => ui.LocalOpen = !ui.LocalOpen);
    public void ToggleRemotesSection() => MutateUi(ui => ui.RemotesOpen = !ui.RemotesOpen);
    public void ToggleStashesSection() => MutateUi(ui => ui.StashesOpen = !ui.StashesOpen);

    public void ToggleRemote(string remoteName) =>
        MutateUi(ui => ui.RemoteOpen[remoteName] = !ui.RemoteOpen.GetValueOrDefault(remoteName, true));

    public void ToggleFolder(BranchFolder folder)
    {
        var key = folder.Key;
        MutateUi(ui => ui.FolderOpen[key] = !ui.FolderOpen.GetValueOrDefault(key, true));
    }

    // ---- expand-all / collapse-all ----
    //
    // Every parent row offers Expand/Collapse All over the node itself and its whole subtree:
    // "Collapse All" closes that node and every collapsible under it, "Expand All" opens them.
    // Missing keys default to open, so collapsing writes explicit `false` entries and expanding
    // writes explicit `true`. A folder, a remote header, and the "Local" section header are the
    // same shape — the root folder of a scope — so they share SetFolderSubtreeOpen; only the
    // "Remotes" and "Stashes" section headers need their own entry points.

    public void SetFolderSubtreeOpen(BranchFolder folder, bool open)
    {
        var names = BranchNamesIn(folder.Scope);
        if (names == null) return;
        MutateUi(ui => SetScopeSubtreeOpen(ui, folder, names, open));
    }

    public void SetRemotesSubtreeOpen(bool open)
    {
        var listing = State.Value.Listing;
        if (listing == null) return;
        MutateUi(ui =>
        {
            ui.RemotesOpen = open;
            foreach (var rg in listing.Remotes)
                SetScopeSubtreeOpen(ui, new BranchFolder(BranchScope.Remote(rg.Name), string.Empty), rg.Branches.Select(b => b.Name), open);
        });
    }

    public void SetStashesSectionOpen(bool open) => MutateUi(ui => ui.StashesOpen = open);

    // Sets `folder` and every collapsible beneath it open/closed within a single ui mutation.
    // A scope's root folder (empty path) has no FolderOpen row of its own — its flag is the
    // header's: the local root flips LocalOpen, a remote root flips RemoteOpen[remote].
    private static void SetScopeSubtreeOpen(BranchesUiState ui, BranchFolder folder, IEnumerable<string> names, bool open)
    {
        if (folder.Path.Length == 0)
        {
            if (folder.Scope.IsRemote) ui.RemoteOpen[folder.Scope.RemoteName!] = open;
            else ui.LocalOpen = open;
        }
        foreach (var path in BranchTreeBuilder.FolderPaths(names))
        {
            if (!IsFolderOrUnder(folder.Path, path)) continue;
            ui.FolderOpen[new BranchFolder(folder.Scope, path).Key] = open;
        }
    }

    private RemoteGroup? FindRemote(string remoteName)
    {
        var listing = State.Value.Listing;
        if (listing == null) return null;
        foreach (var rg in listing.Remotes)
            if (rg.Name == remoteName) return rg;
        return null;
    }

    private IEnumerable<string>? BranchNamesIn(BranchScope scope)
    {
        if (!scope.IsRemote)
            return State.Value.Listing?.LocalBranches.Select(b => b.Name);
        return FindRemote(scope.RemoteName!)?.Branches.Select(b => b.Name);
    }

    // True when the Remotes section has at least one remote, so its menu can hide the
    // Expand/Collapse-All items where they'd be no-ops.
    private bool RemotesHaveCollapsibles()
        => State.Value.Listing is { } listing && listing.Remotes.Count > 0;

    // True when the folder holds at least one branch (directly or nested), so the menu hides
    // Expand/Collapse-All where it'd be a no-op. A named folder always qualifies (folders only
    // exist because a branch created them); only an empty root — a "Local" header or a remote
    // with no branches — comes back false.
    private bool FolderHasChildren(BranchFolder folder)
    {
        var names = BranchNamesIn(folder.Scope);
        if (names == null) return false;
        foreach (var name in names)
            if (BranchIsUnder(folder.Path, name)) return true;
        return false;
    }

    // A branch lives under a folder when the folder is the root (empty path) or the branch name
    // sits below that path. Shared by the has-children check and the cleanup-candidate scan.
    private static bool BranchIsUnder(string folderPath, string branchName)
        => folderPath.Length == 0 || branchName.StartsWith(folderPath + "/", StringComparison.Ordinal);

    // basePath and everything beneath it. The root (empty basePath) covers every folder in
    // its scope (it has no row of its own); a named folder covers itself and its descendants,
    // so collapsing a leaf folder (no sub-folders) still closes the folder itself.
    private static bool IsFolderOrUnder(string basePath, string candidate)
        => basePath.Length == 0
           || candidate == basePath
           || candidate.StartsWith(basePath + "/", StringComparison.Ordinal);

    private void MutateUi(Action<BranchesUiState> mutate)
    {
        Update(s =>
        {
            var ui = s.Ui.Clone();
            mutate(ui);
            return s with { Ui = ui };
        });
        if (_activeRepoId == Guid.Empty) return;
        _registry.SetBranchesUi(_activeRepoId, State.Value.Ui);
    }

    // ---- selection ----

    public void SelectLocalBranch(string fullPath, string tipSha)
        => SelectAndBroadcast(new BranchSelection(IsRemote: false, IsStash: false, RemoteName: null, FullPath: fullPath, TipSha: tipSha));

    public void SelectRemoteBranch(string remoteName, string fullPath, string tipSha)
        => SelectAndBroadcast(new BranchSelection(IsRemote: true, IsStash: false, RemoteName: remoteName, FullPath: fullPath, TipSha: tipSha));

    public void SelectStash(string stashLabel, string tipSha)
        => SelectAndBroadcast(new BranchSelection(IsRemote: false, IsStash: true, RemoteName: null, FullPath: stashLabel, TipSha: tipSha));

    public void ClearSelection()
    {
        if (State.Value.Selection == null) return;
        Update(s => s with { Selection = null });
        _bus.Broadcast(new CommitSelectedMessage(_activeRepoId, null));
    }

    private void SelectAndBroadcast(BranchSelection selection)
    {
        // Switch first: the first switch into History synchronously builds the commits VM, which
        // auto-selects HEAD and broadcasts it. Recording our selection only after that means the
        // HEAD broadcast can't land on (and clear) the branch selection we're about to make.
        SwitchToHistory();
        Update(s => s with { Selection = selection });
        _bus.Broadcast(new CommitSelectedMessage(_activeRepoId, selection.TipSha));
    }

    private void SwitchToHistory()
    {
        if (_mode.Value == MainViewMode.History) return;
        _mode.Value = MainViewMode.History;
    }

    // ---- activation (double-click) ----

    public void ActivateLocalBranch(string fullPath, bool isHead)
    {
        if (State.Value.IsBranchOpInFlight) return;
        if (isHead) return;
        // Branch is checked out in a sibling worktree (or in the primary while a worktree
        // is active) — git will refuse the checkout. Surface the sibling instead so the
        // user can switch context with one click rather than reading a fatal: error.
        if (State.Value.WorktreeBranches.Contains(fullPath))
        {
            SwitchToSiblingHoldingBranch(fullPath);
            return;
        }
        StartCheckoutLocal(fullPath);
    }

    private void SwitchToSiblingHoldingBranch(string branchName)
    {
        var active = _registry.Active.Value;
        if (active is null || active.IsSubmodule) return;
        var primaryId = active.ParentRepoId ?? active.Id;
        foreach (var r in _registry.Repos)
        {
            if (r.Id == active.Id) continue;
            if (r.IsSubmodule) continue;
            var rootId = r.ParentRepoId ?? r.Id;
            if (rootId != primaryId) continue;
            if (string.Equals(r.Branch, branchName, StringComparison.Ordinal))
            {
                _registry.SetActive(r.Id);
                return;
            }
        }
    }

    public void ActivateRemoteBranch(string remoteName, string fullPath)
    {
        if (State.Value.IsBranchOpInFlight) return;
        if (LocalBranchExists(fullPath))
        {
            StartCheckoutLocal(fullPath);
            return;
        }
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new CheckoutBranchDialog
        {
            Repo = repo,
            RemoteName = remoteName,
            RemoteBranchName = fullPath,
            SuggestedLocalName = fullPath,
            OnClose = onClose,
        }));
    }

    // Double-click applies with pop semantics: on a clean apply, prompt to drop the stash.
    public void ActivateStash(int index, string label, string subject)
        => StartApplyStash(index, label, subject, offerDrop: true);

    // Context-menu "Apply" applies and keeps the stash — drop is a separate menu action.
    public void ApplyStash(int index, string label, string subject)
        => StartApplyStash(index, label, subject, offerDrop: false);

    private void StartApplyStash(int index, string label, string subject, bool offerDrop)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;

        TryRunOutcome(
            _stashGen,
            work: () => _gitService.ApplyStash(repo, index),
            onResult: outcome =>
            {
                switch (outcome)
                {
                    case MergeLikeOutcome.Failed failed:
                        _bus.Broadcast(new ShowOperationErrorMessage(_loc.Strings.Value.BranchesErrorStashApplyFailed, failed.Message));
                        return;
                    case MergeLikeOutcome.Conflicted:
                        _bus.Broadcast(new RefsChangedMessage(repo.Id));
                        _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                        return;
                }
                _bus.Broadcast(new RefsChangedMessage(repo.Id));
                _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                if (offerDrop)
                    _bus.Broadcast(new ShowDialogMessage(onClose => new DropStashDialog
                    {
                        Repo = repo,
                        Index = index,
                        Label = label,
                        Subject = subject,
                        OnClose = onClose,
                    }));
            });
    }

    private bool LocalBranchExists(string name)
    {
        var listing = State.Value.Listing;
        if (listing == null) return false;
        foreach (var b in listing.LocalBranches)
            if (string.Equals(b.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    private void StartCheckoutLocal(string branchName)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        if (State.Value.IsBranchOpInFlight) return;

        Update(s => s with { BusyBranch = branchName, PendingHead = branchName });

        RunOutcome(
            work: () => _gitService.CheckoutLocalBranch(repo, branchName),
            onResult: outcome =>
            {
                var failed = outcome as GitOutcome.Failed;
                Update(s => s with { BusyBranch = null, PendingHead = failed == null ? s.PendingHead : null });
                _bus.Broadcast(new RefsChangedMessage(repo.Id));
                _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                if (failed != null)
                    _bus.Broadcast(new ShowOperationErrorMessage(_loc.Strings.Value.BranchesErrorCheckoutFailed, failed.Message));
            },
            lane: _branchOpGen);
    }

    private void StartFastForwardLocal(string branchName, string remoteName, string remoteBranch)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        if (State.Value.IsBranchOpInFlight) return;

        Update(s => s with { BusyBranch = branchName });

        var bus = _bus;

        RunOutcome(
            work: () => _gitService.FastForwardBranch(repo, branchName, remoteName, remoteBranch),
            onResult: outcome =>
            {
                Update(s => s with { BusyBranch = null });
                if (outcome is GitOutcome.Failed failed)
                    bus.Broadcast(new ShowOperationErrorMessage(_loc.Strings.Value.BranchesErrorFastForwardFailed, failed.Message));
                else
                    bus.Broadcast(new RefsChangedMessage(repo.Id));
            },
            lane: _branchOpGen);
    }

    // ---- context menu items (semantic, keyed on the row's identity) ----

    // Menu for the "Local" section header and any local folder, which are the same concept:
    // the header is the root local folder (empty path). "New branch" seeds the dialog's name
    // with the folder's path so the branch is created inside it (empty for the root). Expand/
    // Collapse appears whenever the folder holds any branches — a named folder always does; the
    // root (header) only once a local branch exists (otherwise both items would be no-ops).
    public IReadOnlyList<RepoBarContextMenu.Item> BuildLocalFolderMenu(BranchFolder folder)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        var s = _loc.Strings.Value;
        var namePrefix = folder.Path.Length == 0 ? string.Empty : folder.Path + "/";
        var items = new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item(
                s.BranchesContextNewBranch,
                () => CreateBranch(namePrefix),
                LucideIcons.Branch),
        };
        // "Clean…" only when there's something to clean under this folder, so the menu doesn't
        // open a dialog with an empty branch list.
        if (FolderHasCleanCandidates(folder))
            items.Add(new RepoBarContextMenu.Item(
                s.BranchesContextClean,
                () => OpenCleanDialog(folder),
                LucideIcons.Trash));
        if (FolderHasChildren(folder))
            AppendExpandCollapseItems(items, () => SetFolderSubtreeOpen(folder, true), () => SetFolderSubtreeOpen(folder, false));
        return items;
    }

    // ---- branch cleanup ----
    //
    // "Clean…" gathers the stale local branches under a folder — those whose upstream was
    // deleted (Gone) or that never had one (None) — and hands them to a dialog that
    // confirms and deletes the chosen set. Scoped to the folder's path, so cleaning a sub-folder
    // only touches branches beneath it. The current HEAD and branches checked out in a sibling
    // worktree are excluded — git refuses to delete those.

    public void OpenCleanDialog(BranchFolder folder)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var candidates = BuildCleanCandidates(folder);
        if (candidates.Count == 0) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new CleanBranchesDialog
        {
            Repo = repo,
            FolderPath = folder.Path,
            Candidates = candidates,
            OnClose = onClose,
        }));
    }

    private bool FolderHasCleanCandidates(BranchFolder folder)
    {
        var listing = State.Value.Listing;
        if (listing == null) return false;
        var worktree = State.Value.WorktreeBranches;
        foreach (var b in listing.LocalBranches)
            if (IsCleanCandidate(b, folder, worktree)) return true;
        return false;
    }

    private IReadOnlyList<CleanBranchCandidate> BuildCleanCandidates(BranchFolder folder)
    {
        var listing = State.Value.Listing;
        if (listing == null) return Array.Empty<CleanBranchCandidate>();
        var worktree = State.Value.WorktreeBranches;
        var result = new List<CleanBranchCandidate>();
        foreach (var b in listing.LocalBranches)
        {
            if (!IsCleanCandidate(b, folder, worktree)) continue;
            var kind = b is LocalBranchEntry.Other { Upstream: LocalUpstream.Gone }
                ? BranchCleanupKind.Disconnected
                : BranchCleanupKind.NeverPushed;
            result.Add(new CleanBranchCandidate(b.Name, kind));
        }
        return result;
    }

    // The checked-out branch is excluded structurally: only Other carries a LocalUpstream at all.
    private static bool IsCleanCandidate(LocalBranchEntry b, BranchFolder folder, IReadOnlySet<string> worktree)
        => b is LocalBranchEntry.Other { Upstream: LocalUpstream.Gone or LocalUpstream.None }
           && !worktree.Contains(b.Name)
           && BranchIsUnder(folder.Path, b.Name);

    // Appends a separator followed by Expand All / Collapse All, for menus that already carry
    // actions above the pair (e.g. "New branch", "Edit remote").
    private void AppendExpandCollapseItems(List<RepoBarContextMenu.Item> items, Action expandAll, Action collapseAll)
    {
        items.Add(RepoBarContextMenu.Separator);
        items.AddRange(ExpandCollapseMenu(expandAll, collapseAll));
    }

    // Menu for a remote folder: Expand/Collapse only (no "New branch" — a remote folder groups
    // remote-tracking refs, where seeding a local branch name has no meaning). A remote folder
    // always holds branches, so the items always appear.
    public IReadOnlyList<RepoBarContextMenu.Item> BuildRemoteFolderMenu(BranchFolder folder)
    {
        if (!FolderHasChildren(folder)) return Array.Empty<RepoBarContextMenu.Item>();
        return ExpandCollapseMenu(() => SetFolderSubtreeOpen(folder, true), () => SetFolderSubtreeOpen(folder, false));
    }

    // Menu for the "Stashes" section header: Expand/Collapse the section. Stashes have no nested
    // folders, so this is just the section toggle, offered for consistency with the other headers
    // whenever there's at least one stash (the header row only renders in that case).
    public IReadOnlyList<RepoBarContextMenu.Item> BuildStashesHeaderMenuItems()
    {
        var listing = State.Value.Listing;
        if (listing == null || listing.Stashes.Count == 0) return Array.Empty<RepoBarContextMenu.Item>();
        return ExpandCollapseMenu(() => SetStashesSectionOpen(true), () => SetStashesSectionOpen(false));
    }

    // A standalone Expand All / Collapse All pair (no leading separator), for menus where those
    // are the only items. AppendExpandCollapseItems is the variant that appends to a menu that
    // already has actions above it.
    private List<RepoBarContextMenu.Item> ExpandCollapseMenu(Action expandAll, Action collapseAll)
    {
        var s = _loc.Strings.Value;
        return new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item(s.CommonExpandAll, expandAll, LucideIcons.ChevronDown),
            new RepoBarContextMenu.Item(s.CommonCollapseAll, collapseAll, LucideIcons.ChevronRight),
        };
    }

    // namePrefix pre-fills the dialog's branch-name field (e.g. "feature/admin/" when invoked
    // from a folder), so the new branch is created inside that folder; empty for a plain
    // "New branch".
    public void CreateBranch(string namePrefix = "")
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        // Mirror the toolbar's Branch action: seed the starting point with the current
        // HEAD branch, falling back to "HEAD" when detached (no branch name to seed from).
        var suggested = GetHeadBranchName() ?? "HEAD";
        _bus.Broadcast(new ShowDialogMessage(onClose => new CreateBranchDialog
        {
            Repo = repo,
            SuggestedStartPoint = suggested,
            InitialName = namePrefix,
            OnClose = onClose,
        }));
    }

    // Opens a dedicated Review window for headRef's range of commits. ReviewWindowsViewModel picks
    // up the broadcast and reflects it into a real OS window. BaseRef is left null ("auto" —
    // resolved via merge-base in a later phase).
    public void StartReview(string headRef, string headLabel)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _bus.Broadcast(new OpenReviewWindowMessage(repo.Id, headRef, headLabel, BaseRef: null));
    }

    // Shared inputs for the local-branch context menu, resolved once from the active repo/state.
    private readonly record struct LocalBranchMenu(
        Repo Repo, string Name, bool IsHead, Strings S, BranchesState State,
        bool ThisRowBusy, bool CheckedOutElsewhere, string? HeadBranch, LocalBranchEntry? Entry);

    public IReadOnlyList<RepoBarContextMenu.Item> BuildLocalBranchMenuItems(string fullPath, bool isHead)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        var state = State.Value;
        var menu = new LocalBranchMenu(
            repo, fullPath, isHead, _loc.Strings.Value, state,
            ThisRowBusy: state.BusyBranch == fullPath,
            CheckedOutElsewhere: state.WorktreeBranches.Contains(fullPath),
            HeadBranch: GetHeadBranchName(),
            Entry: FindLocalBranchEntry(fullPath));

        var items = new List<RepoBarContextMenu.Item>();
        AddCheckoutMenuItems(items, menu);
        AddFastForwardMenuItem(items, menu);
        AddMergeRebaseMenuItems(items, menu);
        AddRenameDeleteMenuItems(items, menu);
        return items;
    }

    private void AddCheckoutMenuItems(List<RepoBarContextMenu.Item> items, LocalBranchMenu m)
    {
        var s = m.S;
        var name = m.Name;
        if (m.CheckedOutElsewhere)
            items.Add(new RepoBarContextMenu.Item(
                s.BranchesContextSwitchWorktree,
                () => SwitchToSiblingHoldingBranch(name),
                LucideIcons.Branch));

        var checkoutDisabled = m.IsHead || m.State.IsBranchOpInFlight || m.CheckedOutElsewhere;
        items.Add(new RepoBarContextMenu.Item(
            s.CommonCheckout,
            () => StartCheckoutLocal(name),
            LucideIcons.Branch,
            Enabled: !checkoutDisabled));

        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextReviewChanges,
            () => StartReview(name, name),
            LucideIcons.Search));
    }

    private void AddFastForwardMenuItem(List<RepoBarContextMenu.Item> items, LocalBranchMenu m)
    {
        if (m.IsHead || m.Entry is not LocalBranchEntry.Other { Upstream: LocalUpstream.Tracked tracked }) return;

        var s = m.S;
        var name = m.Name;
        var ffRemote = tracked.Remote;
        var ffBranch = tracked.Branch;
        var ffDisabled = m.ThisRowBusy
            || m.CheckedOutElsewhere
            || m.State.IsBranchOpInFlight
            || tracked.Sync.Behind == 0;
        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextFastForward(ffRemote, ffBranch),
            () => StartFastForwardLocal(name, ffRemote, ffBranch),
            LucideIcons.Pull,
            Enabled: !ffDisabled,
            LabelSegments: BoldSegments(s.BranchesContextFastForward(ffRemote, ffBranch), $"{ffRemote}/{ffBranch}")));
    }

    private void AddMergeRebaseMenuItems(List<RepoBarContextMenu.Item> items, LocalBranchMenu m)
    {
        if (m.HeadBranch == null || m.IsHead) return;

        var s = m.S;
        var repo = m.Repo;
        var name = m.Name;
        var head = m.HeadBranch;
        var canMerge = !m.State.IsBranchOpInFlight;
        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextMerge(name, head),
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new MergeBranchDialog
            {
                Request = new MergeBranchRequest(repo, name, name, head),
                OnClose = onClose,
            })),
            LucideIcons.Merge,
            Enabled: canMerge,
            LabelSegments: BoldSegments(s.BranchesContextMerge(name, head), name, head)));
        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextRebase(head, name),
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new RebaseBranchDialog
            {
                Request = new RebaseBranchRequest(repo, head, name, name),
                OnClose = onClose,
            })),
            LucideIcons.Merge,
            Enabled: canMerge,
            LabelSegments: BoldSegments(s.BranchesContextRebase(head, name), head, name)));
    }

    private void AddRenameDeleteMenuItems(List<RepoBarContextMenu.Item> items, LocalBranchMenu m)
    {
        var s = m.S;
        var repo = m.Repo;
        var name = m.Name;
        var renameDisabled = m.ThisRowBusy;
        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextRename,
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new RenameBranchDialog
            {
                Repo = repo,
                CurrentName = name,
                OnClose = onClose,
            })),
            LucideIcons.PencilLine,
            Enabled: !renameDisabled));

        var deleteDisabled = m.IsHead || m.ThisRowBusy || m.CheckedOutElsewhere;
        var tracked = (m.Entry as LocalBranchEntry.Other)?.Upstream as LocalUpstream.Tracked;
        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextDelete,
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new DeleteLocalBranchDialog
            {
                Repo = repo,
                BranchName = name,
                UpstreamRemote = tracked?.Remote,
                UpstreamBranch = tracked?.Branch,
                OnClose = onClose,
            })),
            LucideIcons.Trash,
            Enabled: !deleteDisabled));
    }

    public IReadOnlyList<RepoBarContextMenu.Item> BuildRemotesHeaderMenuItems()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        var capturedRepo = repo;
        var s = _loc.Strings.Value;
        var items = new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item(
                s.BranchesContextAddRemote,
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new EditRemoteDialog
                {
                    Repo = capturedRepo,
                    OnClose = onClose,
                })),
                LucideIcons.Fetch),
        };
        if (RemotesHaveCollapsibles())
            AppendExpandCollapseItems(items, () => SetRemotesSubtreeOpen(true), () => SetRemotesSubtreeOpen(false));
        return items;
    }

    public IReadOnlyList<RepoBarContextMenu.Item> BuildRemoteHeaderMenuItems(string remoteName)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        var capturedRemote = remoteName;
        var s = _loc.Strings.Value;
        var items = new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item(
                s.BranchesContextEditRemote(capturedRemote),
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new EditRemoteDialog
                {
                    Repo = repo,
                    RemoteName = capturedRemote,
                    OnClose = onClose,
                })),
                LucideIcons.PencilLine),
        };
        var remoteRoot = new BranchFolder(BranchScope.Remote(capturedRemote), string.Empty);
        if (FolderHasChildren(remoteRoot))
            AppendExpandCollapseItems(items, () => SetFolderSubtreeOpen(remoteRoot, true), () => SetFolderSubtreeOpen(remoteRoot, false));
        return items;
    }

    public IReadOnlyList<RepoBarContextMenu.Item> BuildRemoteBranchMenuItems(string remoteName, string fullPath)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        var state = State.Value;
        var checkoutDisabled = state.IsBranchOpInFlight;
        var headBranch = GetHeadBranchName();

        var capturedRepo = repo;
        var capturedRemote = remoteName;
        var capturedName = fullPath;
        var s = _loc.Strings.Value;
        var items = new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item(
                s.CommonCheckout,
                () => ActivateRemoteBranch(capturedRemote, capturedName),
                LucideIcons.Branch,
                Enabled: !checkoutDisabled),
        };

        var remoteRef = $"{capturedRemote}/{capturedName}";
        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextReviewChanges,
            () => StartReview(remoteRef, remoteRef),
            LucideIcons.Search));

        if (headBranch != null)
            AddRemoteMergeRebaseMenuItems(items, s, capturedRepo, remoteRef, headBranch, state.IsBranchOpInFlight);

        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextDeleteRemoteBranch,
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new DeleteRemoteBranchDialog
            {
                Repo = capturedRepo,
                RemoteName = capturedRemote,
                BranchName = capturedName,
                OnClose = onClose,
            })),
            LucideIcons.Trash));
        return items;
    }

    // Merge/rebase the remote branch (display == "<remote>/<name>", also used as the source ref)
    // into the current HEAD branch.
    private void AddRemoteMergeRebaseMenuItems(
        List<RepoBarContextMenu.Item> items, Strings s, Repo repo, string display, string head, bool opInFlight)
    {
        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextMerge(display, head),
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new MergeBranchDialog
            {
                Request = new MergeBranchRequest(repo, display, display, head),
                OnClose = onClose,
            })),
            LucideIcons.Merge,
            Enabled: !opInFlight,
            LabelSegments: BoldSegments(s.BranchesContextMerge(display, head), display, head)));
        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextRebase(head, display),
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new RebaseBranchDialog
            {
                Request = new RebaseBranchRequest(repo, head, display, display),
                OnClose = onClose,
            })),
            LucideIcons.Merge,
            Enabled: !opInFlight,
            LabelSegments: BoldSegments(s.BranchesContextRebase(head, display), head, display)));
    }

    public IReadOnlyList<RepoBarContextMenu.Item> BuildStashMenuItems(int index, string label, string subject)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        var capturedRepo = repo;
        var s = _loc.Strings.Value;
        return new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item(
                s.BranchesContextStashApply,
                () => ApplyStash(index, label, subject),
                LucideIcons.Stash,
                Enabled: !_stashGen.InFlight),
            new RepoBarContextMenu.Item(
                s.BranchesContextRename,
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new RenameStashDialog
                {
                    Repo = capturedRepo,
                    Index = index,
                    CurrentMessage = subject,
                    OnClose = onClose,
                })),
                LucideIcons.PencilLine),
            new RepoBarContextMenu.Item(
                s.BranchesContextDelete,
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new DeleteStashDialog
                {
                    Repo = capturedRepo,
                    Index = index,
                    Subject = subject,
                    OnClose = onClose,
                })),
                LucideIcons.Trash),
        };
    }

    private string? GetHeadBranchName()
    {
        var listing = State.Value.Listing;
        if (listing == null) return null;
        foreach (var b in listing.LocalBranches)
            if (b is LocalBranchEntry.Head) return b.Name;
        return null;
    }

    private LocalBranchEntry? FindLocalBranchEntry(string name)
    {
        var listing = State.Value.Listing;
        if (listing == null) return null;
        foreach (var b in listing.LocalBranches)
            if (b.Name == name) return b;
        return null;
    }

    // Renders a localized, already-formatted menu label with the interpolated values bolded:
    // the catalog owns the wording ("Merge {source} into {target}…") and word order, and we
    // re-find the substituted values in the result to re-create the bold segments. Keeps the
    // emphasis on branch names while letting the surrounding text translate freely.
    private static IReadOnlyList<MenuLabelSegment> BoldSegments(string text, params string[] bold)
    {
        var segments = new List<MenuLabelSegment>();
        var i = 0;
        while (i < text.Length)
        {
            var bestIdx = -1;
            var bestLen = 0;
            foreach (var b in bold)
            {
                if (string.IsNullOrEmpty(b)) continue;
                var idx = text.IndexOf(b, i, StringComparison.Ordinal);
                if (idx >= 0 && (bestIdx < 0 || idx < bestIdx))
                {
                    bestIdx = idx;
                    bestLen = b.Length;
                }
            }

            if (bestIdx < 0)
            {
                segments.Add(new MenuLabelSegment(text.Substring(i)));
                break;
            }

            if (bestIdx > i)
                segments.Add(new MenuLabelSegment(text.Substring(i, bestIdx - i)));
            segments.Add(new MenuLabelSegment(text.Substring(bestIdx, bestLen), Bold: true));
            i = bestIdx + bestLen;
        }

        return segments;
    }

    public override void Dispose()
    {
        _rows.Dispose();
        _rowModels.Dispose();
        _placeholderText.Dispose();
        _contentKind.Dispose();
        base.Dispose();
    }
}

// What the sidebar body shows: the branch tree, the loading skeleton, or an error message.
internal enum BranchesContentKind
{
    List,
    Loading,
    Message,
}

internal sealed record BranchesState(
    BranchListing? Listing,
    BranchesUiState Ui,
    BranchSelection? Selection,
    string? BusyBranch,
    string? PendingHead,
    bool IsLoading,
    string? LoadError,
    IReadOnlySet<string> WorktreeBranches)
{
    // A branch op is in flight whenever BusyBranch is non-null. Reading state should
    // prefer this property over checking BusyBranch directly for intent clarity.
    public bool IsBranchOpInFlight => BusyBranch != null;

    public static BranchesState Initial { get; } = new(
        Listing: null,
        Ui: new BranchesUiState(),
        Selection: null,
        BusyBranch: null,
        PendingHead: null,
        IsLoading: false,
        LoadError: null,
        WorktreeBranches: new HashSet<string>());
}
