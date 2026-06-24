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

    private Guid _activeRepoId;

    // Op lanes for the mutating commands. Checkout and fast-forward share one lane because
    // BusyBranch already serializes them; stash apply runs exclusively on its own lane (it's
    // a distinct concept from branch ops — don't fold it into IsBranchOpInFlight). They are
    // deliberately separate from the default Gen lane, which is only bumped by loads/repo-
    // switch — a mutation must still report its result after a switch.
    private readonly GenerationGuard _branchOpGen;
    private readonly GenerationGuard _stashGen;

    public BranchesViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        State<MainViewMode> mode,
        IRepoSnapshotStore store,
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

        // The listing is projected from the store (which owns loading + caching). OnActiveRepoChanged
        // handles only the per-repo UI bits (fold state, worktree set, selection reset).
        Subscriptions.Add(_registry.Active.Subscribe(_ => OnActiveRepoChanged()));
        Subscriptions.Add(store.Branches.Subscribe(OnStoreBranches));
        Subscriptions.Add(_bus.SubscribeScoped<CommitSelectedMessage>(OnCommitSelected));
        Subscriptions.Add(_bus.SubscribeScoped<WorktreesChangedMessage>(OnWorktreesChanged));
        Subscriptions.Add(_registry.WorktreesChanged.Subscribe(_ => RefreshWorktreeBranches()));
    }

    private void OnWorktreesChanged(WorktreesChangedMessage _) => RefreshWorktreeBranches();

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
                Update(s => s with { Listing = null, IsLoading = false, LoadError = failed.Message, Selection = null });
                _bus.Broadcast(new CommitSelectedMessage(_activeRepoId, null));
                return;
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

        if (State.Value.Selection == null)
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
            if (b.IsHead) return b.Name == branchName;
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

    // ---- expand-all / collapse-all (scoped to a parent's descendants) ----
    //
    // Each method leaves the clicked parent itself open and flips every collapsible
    // descendant within its subtree, so "Collapse All" on a folder hides its sub-folders
    // while keeping the folder you clicked visible. Missing keys default to open, so
    // collapsing writes explicit `false` entries and expanding writes explicit `true`.

    public void SetRemotesDescendantsOpen(bool open)
    {
        var listing = State.Value.Listing;
        if (listing == null) return;
        MutateUi(ui =>
        {
            foreach (var rg in listing.Remotes)
            {
                ui.RemoteOpen[rg.Name] = open;
                foreach (var path in BranchTreeBuilder.FolderPaths(rg.Branches.Select(b => b.Name)))
                    ui.FolderOpen[new BranchFolder(BranchScope.Remote(rg.Name), path).Key] = open;
            }
        });
    }

    public void SetRemoteDescendantsOpen(string remoteName, bool open)
    {
        var rg = FindRemote(remoteName);
        if (rg == null) return;
        MutateUi(ui =>
        {
            foreach (var path in BranchTreeBuilder.FolderPaths(rg.Branches.Select(b => b.Name)))
                ui.FolderOpen[new BranchFolder(BranchScope.Remote(remoteName), path).Key] = open;
        });
    }

    public void SetFolderDescendantsOpen(BranchFolder folder, bool open)
    {
        var names = BranchNamesIn(folder.Scope);
        if (names == null) return;
        MutateUi(ui =>
        {
            foreach (var path in BranchTreeBuilder.FolderPaths(names))
            {
                if (!IsWithinFolder(folder.Path, path)) continue;
                ui.FolderOpen[new BranchFolder(folder.Scope, path).Key] = open;
            }
        });
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
        => State.Value.Listing != null && State.Value.Listing!.Remotes.Count > 0;

    private bool RemoteHasFolders(string remoteName)
    {
        var rg = FindRemote(remoteName);
        return rg != null && BranchTreeBuilder.FolderPaths(rg.Branches.Select(b => b.Name)).Any();
    }

    // True when the folder contains at least one sub-folder (for the root, at least one
    // folder anywhere in its scope), so the menu hides the Expand/Collapse-All items where
    // they'd be no-ops (e.g. a flat local branch list).
    private bool FolderHasSubFolders(BranchFolder folder)
    {
        var names = BranchNamesIn(folder.Scope);
        if (names == null) return false;
        foreach (var path in BranchTreeBuilder.FolderPaths(names))
            if (IsWithinFolder(folder.Path, path)) return true;
        return false;
    }

    // True when candidate is a folder inside basePath. The root (empty basePath) contains
    // every folder in its scope; a named folder contains only strictly deeper paths, never
    // itself — so "Collapse All" on a folder leaves that folder's own row open.
    private static bool IsWithinFolder(string basePath, string candidate)
        => basePath.Length == 0 || candidate.StartsWith(basePath + "/", StringComparison.Ordinal);

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
        Update(s => s with { Selection = selection });
        SwitchToHistory();
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
    // with the folder's path so the branch is created inside it (empty for the root); the
    // Expand/Collapse-All items flip the folder's descendants when it has any.
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
        if (FolderHasSubFolders(folder))
            AppendExpandCollapseItems(items, () => SetFolderDescendantsOpen(folder, true), () => SetFolderDescendantsOpen(folder, false));
        return items;
    }

    // Appends a separator followed by Expand All / Collapse All. Shared by every parent
    // row's menu so the wording, icons, and ordering stay consistent across the tree.
    private void AppendExpandCollapseItems(List<RepoBarContextMenu.Item> items, Action expandAll, Action collapseAll)
    {
        var s = _loc.Strings.Value;
        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(s.CommonExpandAll, expandAll, LucideIcons.ChevronDown));
        items.Add(new RepoBarContextMenu.Item(s.CommonCollapseAll, collapseAll, LucideIcons.ChevronRight));
    }

    // Menu for a remote folder: Expand/Collapse-All only (no "New branch" — a remote folder
    // groups remote-tracking refs, where seeding a local branch name has no meaning).
    public IReadOnlyList<RepoBarContextMenu.Item> BuildRemoteFolderMenu(BranchFolder folder)
    {
        if (State.Value.Listing == null) return Array.Empty<RepoBarContextMenu.Item>();
        if (!FolderHasSubFolders(folder)) return Array.Empty<RepoBarContextMenu.Item>();

        var s = _loc.Strings.Value;
        var items = new List<RepoBarContextMenu.Item>();
        items.Add(new RepoBarContextMenu.Item(s.CommonExpandAll, () => SetFolderDescendantsOpen(folder, true), LucideIcons.ChevronDown));
        items.Add(new RepoBarContextMenu.Item(s.CommonCollapseAll, () => SetFolderDescendantsOpen(folder, false), LucideIcons.ChevronRight));
        return items;
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

    public IReadOnlyList<RepoBarContextMenu.Item> BuildLocalBranchMenuItems(string fullPath, bool isHead)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        var s = _loc.Strings.Value;
        var state = State.Value;
        var thisRowBusy = state.BusyBranch == fullPath;
        var checkedOutElsewhere = state.WorktreeBranches.Contains(fullPath);
        var checkoutDisabled = isHead || state.IsBranchOpInFlight || checkedOutElsewhere;
        var renameDisabled = thisRowBusy;
        var deleteDisabled = isHead || thisRowBusy || checkedOutElsewhere;
        var headBranch = GetHeadBranchName();
        var canMerge = !isHead && headBranch != null && !state.IsBranchOpInFlight;

        var capturedRepo = repo;
        var capturedName = fullPath;
        var items = new List<RepoBarContextMenu.Item>();

        if (checkedOutElsewhere)
        {
            items.Add(new RepoBarContextMenu.Item(
                s.BranchesContextSwitchWorktree,
                () => SwitchToSiblingHoldingBranch(capturedName),
                LucideIcons.Branch));
        }

        items.Add(new RepoBarContextMenu.Item(
            s.CommonCheckout,
            () => StartCheckoutLocal(capturedName),
            LucideIcons.Branch,
            Enabled: !checkoutDisabled));

        var entry = FindLocalBranchEntry(fullPath);
        if (!isHead
            && entry?.UpstreamState == BranchUpstreamState.Tracked
            && !string.IsNullOrEmpty(entry.UpstreamRemote)
            && !string.IsNullOrEmpty(entry.UpstreamBranch))
        {
            var ffRemote = entry.UpstreamRemote;
            var ffBranch = entry.UpstreamBranch;
            var ffDisabled = thisRowBusy
                || checkedOutElsewhere
                || state.IsBranchOpInFlight
                || (entry.BehindBy ?? 0) == 0;
            items.Add(new RepoBarContextMenu.Item(
                s.BranchesContextFastForward(ffRemote, ffBranch),
                () => StartFastForwardLocal(capturedName, ffRemote, ffBranch),
                LucideIcons.Pull,
                Enabled: !ffDisabled,
                LabelSegments: BoldSegments(s.BranchesContextFastForward(ffRemote, ffBranch), $"{ffRemote}/{ffBranch}")));
        }

        if (headBranch != null && !isHead)
        {
            var capturedHead = headBranch;
            items.Add(new RepoBarContextMenu.Item(
                s.BranchesContextMerge(capturedName, capturedHead),
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new MergeBranchDialog
                {
                    Request = new MergeBranchRequest(capturedRepo, capturedName, capturedName, capturedHead),
                    OnClose = onClose,
                })),
                LucideIcons.Merge,
                Enabled: canMerge,
                LabelSegments: BoldSegments(s.BranchesContextMerge(capturedName, capturedHead), capturedName, capturedHead)));
            items.Add(new RepoBarContextMenu.Item(
                s.BranchesContextRebase(capturedHead, capturedName),
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new RebaseBranchDialog
                {
                    Request = new RebaseBranchRequest(capturedRepo, capturedHead, capturedName, capturedName),
                    OnClose = onClose,
                })),
                LucideIcons.Merge,
                Enabled: canMerge,
                LabelSegments: BoldSegments(s.BranchesContextRebase(capturedHead, capturedName), capturedHead, capturedName)));
        }
        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextRename,
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new RenameBranchDialog
            {
                Repo = capturedRepo,
                CurrentName = capturedName,
                OnClose = onClose,
            })),
            LucideIcons.PencilLine,
            Enabled: !renameDisabled));
        var upstreamRemote = entry?.UpstreamState == BranchUpstreamState.Tracked ? entry.UpstreamRemote : null;
        var upstreamBranch = entry?.UpstreamState == BranchUpstreamState.Tracked ? entry.UpstreamBranch : null;
        items.Add(new RepoBarContextMenu.Item(
            s.BranchesContextDelete,
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new DeleteLocalBranchDialog
            {
                Repo = capturedRepo,
                BranchName = capturedName,
                UpstreamRemote = upstreamRemote,
                UpstreamBranch = upstreamBranch,
                OnClose = onClose,
            })),
            LucideIcons.Trash,
            Enabled: !deleteDisabled));

        return items;
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
            AppendExpandCollapseItems(items, () => SetRemotesDescendantsOpen(true), () => SetRemotesDescendantsOpen(false));
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
        if (RemoteHasFolders(capturedRemote))
            AppendExpandCollapseItems(items, () => SetRemoteDescendantsOpen(capturedRemote, true), () => SetRemoteDescendantsOpen(capturedRemote, false));
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

        if (headBranch != null)
        {
            var capturedHead = headBranch;
            var display = $"{capturedRemote}/{capturedName}";
            var sourceRef = display;
            items.Add(new RepoBarContextMenu.Item(
                s.BranchesContextMerge(display, capturedHead),
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new MergeBranchDialog
                {
                    Request = new MergeBranchRequest(capturedRepo, sourceRef, display, capturedHead),
                    OnClose = onClose,
                })),
                LucideIcons.Merge,
                Enabled: !state.IsBranchOpInFlight,
                LabelSegments: BoldSegments(s.BranchesContextMerge(display, capturedHead), display, capturedHead)));
            items.Add(new RepoBarContextMenu.Item(
                s.BranchesContextRebase(capturedHead, display),
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new RebaseBranchDialog
                {
                    Request = new RebaseBranchRequest(capturedRepo, capturedHead, sourceRef, display),
                    OnClose = onClose,
                })),
                LucideIcons.Merge,
                Enabled: !state.IsBranchOpInFlight,
                LabelSegments: BoldSegments(s.BranchesContextRebase(capturedHead, display), capturedHead, display)));
        }

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
            if (b.IsHead) return b.Name;
        return null;
    }

    private BranchEntry? FindLocalBranchEntry(string name)
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
