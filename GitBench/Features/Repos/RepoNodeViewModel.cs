using GitBench.Controls;
using GitBench.Features.ChangeSets;
using GitBench.Features.Notifications;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Which status dot a row shows; error takes priority over dirty.
internal enum RepoRowBadge
{
    None,
    Dirty,
    Error,
}

/// <summary>
/// Backs one RepoBar row and its collapsible subtree. Projects the row's display state
/// (name, missing, active, status dot, expansion) and child rows from the registry/status store by
/// id — so a rename, a missing-flip, or a status change updates the live row without rebuilding it —
/// and owns the row's commands and context menu. Recurses: each node spawns child nodes for its
/// worktrees and submodules.
/// </summary>
internal sealed class RepoNodeViewModel : IDisposable
{
    private readonly Repo _initial;
    private readonly IRepoRegistry _registry;
    private readonly IRepoStatusStore _status;
    private readonly IMessageBus _bus;
    private readonly IGitService _git;
    private readonly IPlatformShell? _shell;
    private readonly ILocalizationService _loc;
    private readonly IClipboard? _clipboard;
    private readonly IUiDispatcher _dispatcher;

    private readonly Derived<Repo?> _currentRepo;
    private readonly Derived<string> _displayName;
    private readonly Derived<bool> _isMissing;
    private readonly Derived<bool> _isActive;
    private readonly Derived<RepoRowBadge> _badge;
    private readonly Derived<bool> _hasChildren;
    private readonly Derived<bool> _canActivate;
    private readonly Derived<IReadOnlyList<Repo>> _childRepos;
    private readonly KeyedViewModelList<Repo, Guid, RepoNodeViewModel> _children;
    private readonly Derived<TreeGuides> _guides;
    private readonly Derived<int?> _hotkeyDigit;
    private readonly Derived<bool> _isRenaming;

    public Guid RepoId => _initial.Id;
    public RepoKind Kind => _initial.Kind;
    public int Depth { get; }

    // The live repo record (reflects renames / missing changes); falls back to the seed if the repo
    // has left the registry mid-teardown.
    public Repo Repo => _currentRepo.Value ?? _initial;

    public IReadable<string> DisplayName => _displayName;
    // True while this primary row's name is being edited inline. Always false for worktree/submodule
    // rows, which aren't renamable.
    public IReadable<bool> IsRenaming => _isRenaming;
    public IReadable<bool> IsMissing => _isMissing;
    public IReadable<bool> IsActive => _isActive;
    public IReadable<RepoRowBadge> Badge => _badge;

    // The 1-9 keyboard slot this repo occupies, or null. Only primaries are assignable, so a worktree
    // or submodule row always reads null and shows no badge.
    public IReadable<int?> HotkeyDigit => _hotkeyDigit;

    public IReadable<bool> HasChildren => _hasChildren;
    public IReadable<bool> IsExpanded { get; }
    public ObservableList<RepoNodeViewModel> Children => _children.Items;

    // The ancestry connectors this row draws (worktrees/submodules under their primary, recursively).
    public IReadable<TreeGuides> Guides => _guides;

    // Folds the row's subtree. Gated on HasChildren, so a childless row's chevron is inert — its click
    // falls through to the row instead of being swallowed.
    public ICommand ToggleExpand { get; }

    // Makes this repo the active one. Gated for a missing submodule (no working tree to open);
    // primaries and worktrees always activate.
    public ICommand Activate { get; }

    public RepoNodeViewModel(
        Repo repo,
        int depth,
        IRepoRegistry registry,
        IRepoStatusStore status,
        IMessageBus bus,
        IGitService git,
        IPlatformShell? shell,
        ILocalizationService loc,
        IClipboard? clipboard,
        IUiDispatcher dispatcher,
        RepoNodeFactory factory)
    {
        _initial = repo;
        Depth = depth;
        _registry = registry;
        _status = status;
        _bus = bus;
        _git = git;
        _shell = shell;
        _loc = loc;
        _clipboard = clipboard;
        _dispatcher = dispatcher;

        IsExpanded = registry.WatchWorktreeExpanded(repo.Id);

        _currentRepo = new Derived<Repo?>(() => FindRepo(RepoId));
        _displayName = new Derived<string>(() => _currentRepo.Value?.DisplayName ?? _initial.DisplayName);
        _isMissing = new Derived<bool>(() => _currentRepo.Value?.IsMissing ?? _initial.IsMissing);
        _isActive = new Derived<bool>(() => _registry.Active.Value?.Id == RepoId);
        _badge = new Derived<RepoRowBadge>(() =>
        {
            var st = _status.For(RepoId);
            return st.HasUnseenError ? RepoRowBadge.Error : st.IsDirty ? RepoRowBadge.Dirty : RepoRowBadge.None;
        });
        _hasChildren = new Derived<bool>(() => HasAnyChild(RepoId));
        _canActivate = new Derived<bool>(() => Kind != RepoKind.Submodule || !IsMissing.Value);

        _childRepos = new Derived<IReadOnlyList<Repo>>(ComputeChildRepos);
        _children = new KeyedViewModelList<Repo, Guid, RepoNodeViewModel>(
            _childRepos, r => r.Id, r => factory.Create(r, depth + 1));
        _guides = new Derived<TreeGuides>(() => new TreeGuides(ComputeGuideMask(), Depth + 1));
        _hotkeyDigit = new Derived<int?>(() =>
        {
            _ = _registry.HotkeysChanged.Value;
            return _registry.HotkeyFor(RepoId);
        });
        _isRenaming = new Derived<bool>(() => _registry.RenamingRepoId.Value == RepoId);

        ToggleExpand = new Command(() => _registry.SetWorktreeExpanded(RepoId, !IsExpanded.Value), HasChildren);
        Activate = new Command(() => _registry.SetActive(RepoId), _canActivate);
    }

    // Worktrees first, then submodules — both recurse, same order/indent at every level. Empty while
    // collapsed so a folded subtree builds no rows (and spawns no child view models).
    private IReadOnlyList<Repo> ComputeChildRepos()
    {
        if (!IsExpanded.Value) return Array.Empty<Repo>();
        var repos = new List<Repo>();
        foreach (var r in _registry.Repos)
            if (r.ParentRepoId == RepoId && r.IsWorktree) repos.Add(r);
        foreach (var r in _registry.Repos)
            if (r.ParentRepoId == RepoId && r.IsSubmodule) repos.Add(r);
        return repos;
    }

    private bool HasAnyChild(Guid parentId)
    {
        foreach (var r in _registry.Repos)
            if (r.ParentRepoId == parentId) return true;
        return false;
    }

    // This row's guides. Level k is the path node at depth k (level 0 is the group header's child — the
    // top-level primary). The deepest level (this row) draws its own connector — a corner if it is the
    // last child, else a tee — and each shallower level a passthrough trunk while that ancestor still has
    // a sibling below it.
    private long ComputeGuideMask()
    {
        long mask = 0;
        var node = _currentRepo.Value ?? _initial;
        for (var depth = Depth; depth >= 0; depth--)
        {
            var last = IsLastSibling(node);
            mask = TreeGuides.SetKind(mask, depth, depth == Depth
                ? (last ? TreeGuide.Corner : TreeGuide.Tee)
                : (last ? TreeGuide.None : TreeGuide.Through));
            if (depth == 0) break;
            if (node.ParentRepoId is not { } pid || FindRepo(pid) is not { } parent) break;
            node = parent;
        }
        return mask;
    }

    // Whether a repo is the last among its siblings, in the same order the rows render: worktrees then
    // submodules under a parent repo, or the group's repo order for a top-level primary.
    private bool IsLastSibling(Repo repo)
    {
        if (repo.ParentRepoId is { } pid)
        {
            Repo? last = null;
            foreach (var r in _registry.Repos)
                if (r.ParentRepoId == pid && r.IsWorktree) last = r;
            foreach (var r in _registry.Repos)
                if (r.ParentRepoId == pid && r.IsSubmodule) last = r;
            return last is null || last.Id == repo.Id;
        }

        var group = _registry.FindGroupContaining(repo.Id);
        if (group is null) return true;
        var ids = group.RepoIds;
        return ids.Count == 0 || ids[ids.Count - 1] == repo.Id;
    }

    private Repo? FindRepo(Guid id)
    {
        foreach (var r in _registry.Repos)
            if (r.Id == id) return r;
        return null;
    }

    public IReadOnlyList<RepoBarContextMenu.Item> BuildMenuItems() => Kind switch
    {
        RepoKind.Worktree => BuildWorktreeMenu(),
        RepoKind.Submodule => BuildSubmoduleMenu(),
        _ => BuildPrimaryMenu(),
    };

    private IReadOnlyList<RepoBarContextMenu.Item> BuildPrimaryMenu()
    {
        var repo = Repo;
        var s = _loc.Strings.Value;
        var items = new List<RepoBarContextMenu.Item>();

        AddPrimaryRepoActions(items, s, repo);
        AddStartChangeSetItem(items, s, repo);
        AddHotkeyMenu(items, s, repo);
        AddWorktreeMenu(items, s, repo);
        AddSubmoduleMenu(items, s, repo);
        AddMoveToGroupMenu(items, s, repo);
        return items;
    }

    // Primary actions first — the common ones a right-click is usually reaching for.
    private void AddPrimaryRepoActions(List<RepoBarContextMenu.Item> items, Strings s, Repo repo)
    {
        items.Add(new RepoBarContextMenu.Item(s.ReposRepoRename, () => _registry.BeginRenameRepo(repo.Id), LucideIcons.PencilLine));
        items.Add(new RepoBarContextMenu.Item(s.ReposRepoRemove, () => _registry.RemoveRepo(repo.Id), LucideIcons.Trash));
        if (_clipboard is not null)
            items.Add(new RepoBarContextMenu.Item(s.ReposRepoCopyPath, () => CopyPath(repo.Path), LucideIcons.Copy));
        if (_shell is not null)
            items.Add(new RepoBarContextMenu.Item(s.CommonOpenFolder, () => _shell.OpenFolder(repo.Path), LucideIcons.FolderOpen));
        AddOpenRemoteItem(items, s, repo);
    }

    // Start change set… — create a same-named branch across this repo's group primaries (Phase 4.1).
    // Offered only when the containing group has two or more primaries; opens the same dialog as the
    // group header, its checklist defaulting to all of them.
    private void AddStartChangeSetItem(List<RepoBarContextMenu.Item> items, Strings s, Repo repo)
    {
        var group = _registry.FindGroupContaining(repo.Id);
        if (group is null) return;
        var primaries = _registry.PrimariesOfGroup(group);
        if (primaries.Count < 2) return;

        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            s.ChangesetsStartMenu,
            () => _bus.Broadcast(new ShowDialogMessage(
                onClose => new StartChangeSetDialog { Repos = primaries, OnClose = onClose })),
            LucideIcons.FolderGit2));
    }

    private void AddHotkeyMenu(List<RepoBarContextMenu.Item> items, Strings s, Repo repo)
    {
        var currentSlot = _registry.HotkeyFor(repo.Id);
        var slotItems = new List<RepoBarContextMenu.Item>(9);
        for (var n = 1; n <= 9; n++)
        {
            var slot = n;
            var holder = _registry.RepoForHotkey(slot);
            // The repo that currently holds this slot (when it isn't us), surfaced as a hint so a
            // pick that would steal the slot is visible beforehand.
            var holderName = holder is { } hid && hid != repo.Id ? FindRepo(hid)?.DisplayName : null;
            slotItems.Add(new RepoBarContextMenu.Item(
                slot.ToString(),
                () => _registry.AssignHotkey(repo.Id, slot),
                Icon: currentSlot == slot ? LucideIcons.CircleCheck : null,
                Shortcut: holderName));
        }

        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(s.ReposHotkeyAssign, () => { }, LucideIcons.SquareTerminal,
            Submenu: slotItems, SubmenuMinWidth: 180f));
        if (currentSlot is { } assigned)
            items.Add(new RepoBarContextMenu.Item(s.ReposHotkeyClear, () => _registry.ClearHotkey(assigned), LucideIcons.X));
    }

    private void AddWorktreeMenu(List<RepoBarContextMenu.Item> items, Strings s, Repo repo)
    {
        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(s.ReposRepoNewWorktree,
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new CreateWorktreeDialog { Primary = repo, OnClose = onClose })),
            LucideIcons.Branch));
        items.Add(new RepoBarContextMenu.Item(s.ReposRepoPruneWorktrees,
            () =>
            {
                Task.Run(() => _git.PruneWorktrees(repo));
                _bus.Broadcast(new WorktreesChangedMessage(repo.Id));
            },
            LucideIcons.Trash));
    }

    private void AddSubmoduleMenu(List<RepoBarContextMenu.Item> items, Strings s, Repo repo)
    {
        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(s.ReposRepoAddSubmodule,
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new AddSubmoduleDialog { Primary = repo, OnClose = onClose })),
            LucideIcons.Package));
        items.Add(new RepoBarContextMenu.Item(s.ReposRepoUpdateAllSubmodules,
            () => _bus.Broadcast(new ShowDialogMessage(onClose => new UpdateSubmodulesDialog { Primary = repo, Target = null, OnClose = onClose })),
            LucideIcons.Pull));
    }

    // "Move to <group>" last, in its own section — this list grows and shrinks with the group set,
    // so keeping it at the bottom leaves the fixed actions above it stable.
    private void AddMoveToGroupMenu(List<RepoBarContextMenu.Item> items, Strings s, Repo repo)
    {
        var sourceGroup = _registry.FindGroupContaining(repo.Id);
        var moveTargets = _registry.Groups.Where(g => sourceGroup == null || g.Id != sourceGroup.Id).ToList();
        if (moveTargets.Count == 0) return;

        items.Add(RepoBarContextMenu.Separator);
        foreach (var group in moveTargets)
        {
            var captured = group;
            items.Add(new RepoBarContextMenu.Item(
                s.ReposRepoMoveToGroup(captured.Name.Value),
                () => _registry.MoveRepo(repo.Id, captured.Id, captured.RepoIds.Count),
                LucideIcons.FolderInput));
        }
    }

    private void AddOpenRemoteItem(List<RepoBarContextMenu.Item> items, Strings s, Repo repo)
    {
        if (_shell is null) return;
        items.Add(new RepoBarContextMenu.Item(s.ReposRepoOpenRemote, () => OpenRemote(repo), LucideIcons.ExternalLink));
    }

    // Reads the remote URL off-thread (a git process spawn) and hands it to the browser. "origin"
    // wins when several remotes exist; a repo with no web-openable remote surfaces the error dialog.
    private void OpenRemote(Repo repo)
    {
        var shell = _shell;
        if (shell is null) return;
        Task.Run(() =>
        {
            var remotes = _git.GetRemoteNames(repo);
            var remoteName = remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
            var rawUrl = remoteName is null ? null : _git.GetRemoteUrl(repo, remoteName);
            var webUrl = rawUrl is null ? null : RemoteWebUrl.FromRemoteUrl(rawUrl);
            if (webUrl is not null)
            {
                shell.OpenUrl(webUrl);
                return;
            }
            _dispatcher.Post(() =>
            {
                var s = _loc.Strings.Value;
                _bus.Broadcast(new ShowOperationErrorMessage(
                    s.ReposErrorOpenRemoteFailed,
                    rawUrl is null ? s.ReposErrorNoRemoteUrl : s.ReposErrorRemoteUrlNotWeb(rawUrl)));
            });
        });
    }

    private void CopyPath(string path)
    {
        _clipboard?.SetText(path);
        _bus.Broadcast(new ShowToastMessage(ToastIntent.Success(_loc.Strings.Value.ToastCopiedPath)));
    }

    private IReadOnlyList<RepoBarContextMenu.Item> BuildWorktreeMenu()
    {
        var worktree = Repo;
        var s = _loc.Strings.Value;
        var items = new List<RepoBarContextMenu.Item>
        {
            new(s.ReposWorktreeSwitchTo, () => _registry.SetActive(worktree.Id), LucideIcons.Branch),
        };

        if (_shell is not null)
            items.Add(new RepoBarContextMenu.Item(s.CommonOpenFolder, () => _shell.OpenFolder(worktree.Path), LucideIcons.FolderOpen));
        AddOpenRemoteItem(items, s, worktree);

        if (worktree.ParentRepoId is { } parentId && FindRepo(parentId) is { } primary)
        {
            items.Add(new RepoBarContextMenu.Item(
                s.ReposWorktreeRemove,
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new RemoveWorktreeDialog { Primary = primary, Worktree = worktree, OnClose = onClose })),
                LucideIcons.Trash));
        }

        return items;
    }

    private IReadOnlyList<RepoBarContextMenu.Item> BuildSubmoduleMenu()
    {
        var submodule = Repo;
        var s = _loc.Strings.Value;
        var items = new List<RepoBarContextMenu.Item>();

        if (!submodule.IsMissing)
            items.Add(new RepoBarContextMenu.Item(s.ReposSubmoduleSwitchTo, () => _registry.SetActive(submodule.Id), LucideIcons.Package));

        if (_shell is not null)
            items.Add(new RepoBarContextMenu.Item(s.CommonOpenFolder, () => _shell.OpenFolder(submodule.Path), LucideIcons.FolderOpen));
        if (!submodule.IsMissing)
            AddOpenRemoteItem(items, s, submodule);

        if (submodule.ParentRepoId is { } parentId && FindRepo(parentId) is { } primary)
        {
            items.Add(new RepoBarContextMenu.Item(
                s.ReposSubmoduleUpdate,
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new UpdateSubmodulesDialog { Primary = primary, Target = submodule, OnClose = onClose })),
                LucideIcons.Pull));
            items.Add(new RepoBarContextMenu.Item(
                s.ReposSubmoduleDeinit,
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new DeinitSubmoduleDialog { Primary = primary, Submodule = submodule, OnClose = onClose })),
                LucideIcons.Trash));
        }

        return items;
    }

    public void Dispose()
    {
        _isRenaming.Dispose();
        _hotkeyDigit.Dispose();
        _guides.Dispose();
        _children.Dispose();
        _childRepos.Dispose();
        _canActivate.Dispose();
        _hasChildren.Dispose();
        _badge.Dispose();
        _isActive.Dispose();
        _isMissing.Dispose();
        _displayName.Dispose();
        _currentRepo.Dispose();
    }
}
