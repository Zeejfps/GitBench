using ZGF.Observable;

namespace GitGui;

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

    public IReadable<BranchListing?> Listing { get; }
    public IReadable<BranchesUiState> Ui { get; }
    public IReadable<BranchSelection?> Selection { get; }
    public IReadable<string?> BusyBranch { get; }
    public IReadable<string?> LoadError { get; }
    public IReadable<bool> IsLoading { get; }
    public IReadable<IReadOnlySet<string>> WorktreeBranches { get; }

    private Guid _activeRepoId;

    // Stash apply uses its own flag — it's a distinct concept from branch ops, and the
    // existing presenter treated it that way. Don't fold it into IsBranchOpInFlight.
    private bool _isStashApplying;

    // Op lanes for the mutating commands. Checkout and fast-forward share one lane because
    // BusyBranch already serializes them; stash apply has its own (it runs independently of
    // branch ops). They are deliberately separate from the default Gen lane, which is only
    // bumped by loads/repo-switch — a mutation must still report its result after a switch.
    private readonly GenerationGuard _branchOpGen;
    private readonly GenerationGuard _stashGen;

    public BranchesViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        State<MainViewMode> mode)
        : base(dispatcher, BranchesState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _mode = mode;

        _branchOpGen = CreateLane();
        _stashGen = CreateLane();

        Listing = Slice(s => s.Listing);
        Ui = Slice(s => s.Ui);
        Selection = Slice(s => s.Selection);
        BusyBranch = Slice(s => s.BusyBranch);
        LoadError = Slice(s => s.LoadError);
        IsLoading = Slice(s => s.IsLoading);
        WorktreeBranches = Slice(s => s.WorktreeBranches);

        Subscriptions.Add(_registry.Active.Subscribe(_ => OnActiveRepoChanged()));
        Subscriptions.Add(_bus.SubscribeScoped<CommitCreatedMessage>(OnCommitCreated));
        Subscriptions.Add(_bus.SubscribeScoped<CommitSelectedMessage>(OnCommitSelected));
        Subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(OnRefsChanged));
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
        Update(_ => new BranchesState(Listing: null, Ui: ui, Selection: null, BusyBranch: null, IsLoading: true, LoadError: null, WorktreeBranches: EmptyStringSet));
        RefreshWorktreeBranches();
        StartLoad(active);
    }

    private void OnCommitCreated(CommitCreatedMessage msg)
    {
        var active = _registry.Active.Value;
        if (active == null || active.Id != msg.RepoId) return;
        StartLoad(active);
    }

    private void OnRefsChanged(RefsChangedMessage msg)
    {
        var active = _registry.Active.Value;
        if (active == null || active.Id != msg.RepoId) return;
        StartLoad(active);
    }

    private void OnCommitSelected(CommitSelectedMessage msg)
    {
        if (msg.RepoId != _activeRepoId) return;
        var current = State.Value.Selection;
        if (current == null) return;
        if (msg.Sha == current.Value.TipSha) return;
        Update(s => s with { Selection = null });
    }

    private void StartLoad(Repo repo)
    {
        Update(s => s.LoadError != null ? s with { LoadError = null } : s);

        RunBackground<BranchListing>(
            work: () => (_gitService.GetBranches(repo), null),
            onResult: (listing, error) =>
            {
                if (repo.Id != _activeRepoId) return;
                // ApplyListing's error path keys off BranchListing.ErrorMessage, so wrap
                // RunBackground's separate `error` channel back into a synthetic listing.
                var applied = error != null
                    ? new BranchListing(repo.Id, Array.Empty<BranchEntry>(), Array.Empty<RemoteGroup>(), Array.Empty<StashEntry>(), error)
                    : listing!;
                ApplyListing(applied);
            });
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

            return s with
            {
                Listing = listing.ErrorMessage == null ? listing : null,
                LoadError = listing.ErrorMessage,
                IsLoading = false,
                Selection = selection,
            };
        });

        if (State.Value.Selection == null)
            _bus.Broadcast(new CommitSelectedMessage(_activeRepoId, null));
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

    // ---- section/folder toggles ----

    public void ToggleLocalSection() => MutateUi(ui => ui.LocalOpen = !ui.LocalOpen);
    public void ToggleRemotesSection() => MutateUi(ui => ui.RemotesOpen = !ui.RemotesOpen);
    public void ToggleStashesSection() => MutateUi(ui => ui.StashesOpen = !ui.StashesOpen);

    public void ToggleRemote(string remoteName) =>
        MutateUi(ui => ui.RemoteOpen[remoteName] = !ui.RemoteOpen.GetValueOrDefault(remoteName, true));

    public void ToggleFolder(string key) =>
        MutateUi(ui => ui.FolderOpen[key] = !ui.FolderOpen.GetValueOrDefault(key, true));

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
        _bus.Broadcast(new ShowDialogMessage(onClose => new CheckoutBranchDialog(
            repo, remoteName, fullPath, fullPath, onClose)));
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
        if (_isStashApplying) return;

        _isStashApplying = true;

        RunBackground<StashOutcome>(
            work: () => (_gitService.ApplyStash(repo, index), null),
            onResult: (outcome, error) =>
            {
                _isStashApplying = false;
                if (error != null || !outcome!.Success)
                {
                    _bus.Broadcast(new ShowOperationErrorMessage(
                        "Stash apply failed",
                        (error ?? outcome?.ErrorMessage) ?? "Stash apply failed."));
                    return;
                }
                _bus.Broadcast(new RefsChangedMessage(repo.Id));
                _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                if (outcome.HasConflicts) return;
                if (offerDrop)
                    _bus.Broadcast(new ShowDialogMessage(onClose => new DropStashDialog(
                        repo, index, label, subject, onClose)));
            },
            lane: _stashGen);
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

        Update(s => s with { BusyBranch = branchName });

        RunBackground<CheckoutOutcome>(
            work: () => (_gitService.CheckoutLocalBranch(repo, branchName), null),
            onResult: (outcome, error) =>
            {
                Update(s => s with { BusyBranch = null });
                if (error == null && outcome!.Success)
                    _bus.Broadcast(new RefsChangedMessage(repo.Id));
                else
                    _bus.Broadcast(new ShowOperationErrorMessage(
                        "Checkout failed",
                        (error ?? outcome?.ErrorMessage) ?? "Checkout failed."));
            },
            lane: _branchOpGen);
    }

    private void StartFastForwardLocal(string branchName, string remoteName, string remoteBranch)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        if (State.Value.IsBranchOpInFlight) return;

        Update(s => s with { BusyBranch = branchName });

        var dispatcher = Dispatcher;
        var bus = _bus;

        var opId = Guid.NewGuid();
        bus.Broadcast(new OperationStartedMessage(
            opId,
            $"Fast-forward {branchName} ← {remoteName}/{remoteBranch}",
            LucideIcons.Pull));

        // Progress streams on a side-channel: the worker posts each git line straight to the
        // operations presenter. It is intentionally unguarded — RunBackground's lane only
        // gates the single terminal result below.
        void OnLineFromWorker(string line)
        {
            dispatcher.Post(() =>
            {
                var (phase, percent) = GitProgressParser.Parse(line);
                bus.Broadcast(new OperationProgressMessage(opId, phase, percent, line));
            });
        }

        RunBackground<FastForwardOutcome>(
            work: () => (_gitService.FastForwardBranch(repo, branchName, remoteName, remoteBranch, OnLineFromWorker), null),
            onResult: (outcome, error) =>
            {
                Update(s => s with { BusyBranch = null });
                var success = error == null && outcome!.Success;
                var errorMessage = error ?? outcome?.ErrorMessage;
                bus.Broadcast(new OperationFinishedMessage(opId, success, errorMessage));
                if (success)
                    bus.Broadcast(new RefsChangedMessage(repo.Id));
                else
                    bus.Broadcast(new ShowOperationErrorMessage(
                        "Fast-forward failed",
                        errorMessage ?? "Fast-forward failed."));
            },
            lane: _branchOpGen);
    }

    // ---- context menu items (semantic, keyed on the row's identity) ----

    public IReadOnlyList<RepoBarContextMenu.Item> BuildLocalHeaderMenuItems()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        return new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item(
                "New branch…",
                CreateBranch,
                LucideIcons.Branch),
        };
    }

    public void CreateBranch()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        // Mirror the toolbar's Branch action: seed the starting point with the current
        // HEAD branch, falling back to "HEAD" when detached (no branch name to seed from).
        var suggested = GetHeadBranchName() ?? "HEAD";
        _bus.Broadcast(new ShowDialogMessage(onClose => new CreateBranchDialog(repo, suggested, onClose)));
    }

    public IReadOnlyList<RepoBarContextMenu.Item> BuildLocalBranchMenuItems(string fullPath, bool isHead)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

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
                "Switch to worktree",
                () => SwitchToSiblingHoldingBranch(capturedName),
                LucideIcons.Branch));
        }

        items.Add(new RepoBarContextMenu.Item(
            "Checkout",
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
                $"Fast-forward to '{ffRemote}/{ffBranch}'",
                () => StartFastForwardLocal(capturedName, ffRemote, ffBranch),
                LucideIcons.Pull,
                Enabled: !ffDisabled,
                LabelSegments: BuildFastForwardSegments(ffRemote, ffBranch)));
        }

        if (headBranch != null && !isHead)
        {
            var capturedHead = headBranch;
            items.Add(new RepoBarContextMenu.Item(
                $"Merge {capturedName} into {capturedHead}…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new MergeBranchDialog(
                    new MergeBranchRequest(capturedRepo, capturedName, capturedName, capturedHead), onClose))),
                LucideIcons.Merge,
                Enabled: canMerge,
                LabelSegments: BuildMergeSegments(capturedName, capturedHead)));
            items.Add(new RepoBarContextMenu.Item(
                $"Rebase {capturedHead} onto {capturedName}…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new RebaseBranchDialog(
                    new RebaseBranchRequest(capturedRepo, capturedHead, capturedName, capturedName), onClose))),
                LucideIcons.Merge,
                Enabled: canMerge,
                LabelSegments: BuildRebaseSegments(capturedHead, capturedName)));
        }
        items.Add(new RepoBarContextMenu.Item(
            "Rename…",
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new RenameBranchDialog(capturedRepo, capturedName, onClose))),
            LucideIcons.PencilLine,
            Enabled: !renameDisabled));
        var upstreamRemote = entry?.UpstreamState == BranchUpstreamState.Tracked ? entry.UpstreamRemote : null;
        var upstreamBranch = entry?.UpstreamState == BranchUpstreamState.Tracked ? entry.UpstreamBranch : null;
        items.Add(new RepoBarContextMenu.Item(
            "Delete…",
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new DeleteLocalBranchDialog(
                capturedRepo, capturedName, upstreamRemote, upstreamBranch, onClose))),
            LucideIcons.Trash,
            Enabled: !deleteDisabled));

        return items;
    }

    public IReadOnlyList<RepoBarContextMenu.Item> BuildRemoteHeaderMenuItems(string remoteName)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        var capturedRemote = remoteName;
        return new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item(
                $"Edit {capturedRemote}…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new EditRemoteDialog(
                    repo, capturedRemote, onClose))),
                LucideIcons.PencilLine),
        };
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
        var items = new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item(
                "Checkout",
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
                $"Merge {display} into {capturedHead}…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new MergeBranchDialog(
                    new MergeBranchRequest(capturedRepo, sourceRef, display, capturedHead), onClose))),
                LucideIcons.Merge,
                Enabled: !state.IsBranchOpInFlight,
                LabelSegments: BuildMergeSegments(display, capturedHead)));
            items.Add(new RepoBarContextMenu.Item(
                $"Rebase {capturedHead} onto {display}…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new RebaseBranchDialog(
                    new RebaseBranchRequest(capturedRepo, capturedHead, sourceRef, display), onClose))),
                LucideIcons.Merge,
                Enabled: !state.IsBranchOpInFlight,
                LabelSegments: BuildRebaseSegments(capturedHead, display)));
        }

        items.Add(new RepoBarContextMenu.Item(
            "Delete remote branch…",
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new DeleteRemoteBranchDialog(
                capturedRepo, capturedRemote, capturedName, onClose))),
            LucideIcons.Trash));
        return items;
    }

    public IReadOnlyList<RepoBarContextMenu.Item> BuildStashMenuItems(int index, string label, string subject)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return Array.Empty<RepoBarContextMenu.Item>();

        var capturedRepo = repo;
        return new List<RepoBarContextMenu.Item>
        {
            new RepoBarContextMenu.Item(
                "Apply",
                () => ApplyStash(index, label, subject),
                LucideIcons.Stash,
                Enabled: !_isStashApplying),
            new RepoBarContextMenu.Item(
                "Rename…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new RenameStashDialog(
                    capturedRepo, index, subject, onClose))),
                LucideIcons.PencilLine),
            new RepoBarContextMenu.Item(
                "Delete…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new DeleteStashDialog(
                    capturedRepo, index, subject, onClose))),
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

    private static IReadOnlyList<MenuLabelSegment> BuildMergeSegments(string source, string target) =>
    [
        new MenuLabelSegment("Merge "),
        new MenuLabelSegment(source, Bold: true),
        new MenuLabelSegment(" into "),
        new MenuLabelSegment(target, Bold: true),
        new MenuLabelSegment("…"),
    ];

    private static IReadOnlyList<MenuLabelSegment> BuildRebaseSegments(string source, string target) =>
    [
        new MenuLabelSegment("Rebase "),
        new MenuLabelSegment(source, Bold: true),
        new MenuLabelSegment(" onto "),
        new MenuLabelSegment(target, Bold: true),
        new MenuLabelSegment("…"),
    ];

    private static IReadOnlyList<MenuLabelSegment> BuildFastForwardSegments(string remote, string branch) =>
    [
        new MenuLabelSegment("Fast-forward to '"),
        new MenuLabelSegment($"{remote}/{branch}", Bold: true),
        new MenuLabelSegment("'"),
    ];
}

internal sealed record BranchesState(
    BranchListing? Listing,
    BranchesUiState Ui,
    BranchSelection? Selection,
    string? BusyBranch,
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
        IsLoading: false,
        LoadError: null,
        WorktreeBranches: new HashSet<string>());
}
