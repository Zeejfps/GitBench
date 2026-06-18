using GitBench.Controls;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using GitBench.Platform;
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

    private readonly Derived<Repo?> _currentRepo;
    private readonly Derived<string> _displayName;
    private readonly Derived<bool> _isMissing;
    private readonly Derived<bool> _isActive;
    private readonly Derived<RepoRowBadge> _badge;
    private readonly Derived<bool> _hasChildren;
    private readonly Derived<IReadOnlyList<Repo>> _childRepos;
    private readonly KeyedViewModelList<Repo, Guid, RepoNodeViewModel> _children;

    public Guid RepoId => _initial.Id;
    public RepoKind Kind => _initial.Kind;
    public int Depth { get; }

    // The live repo record (reflects renames / missing changes); falls back to the seed if the repo
    // has left the registry mid-teardown.
    public Repo Repo => _currentRepo.Value ?? _initial;

    public IReadable<string> DisplayName => _displayName;
    public IReadable<bool> IsMissing => _isMissing;
    public IReadable<bool> IsActive => _isActive;
    public IReadable<RepoRowBadge> Badge => _badge;

    public IReadable<bool> HasChildren => _hasChildren;
    public IReadable<bool> IsExpanded { get; }
    public ObservableList<RepoNodeViewModel> Children => _children.Items;

    // Folds the row's subtree. Gated on HasChildren, so a childless row's chevron is inert — its click
    // falls through to the row instead of being swallowed.
    public ICommand ToggleExpand { get; }

    public RepoNodeViewModel(
        Repo repo,
        int depth,
        IRepoRegistry registry,
        IRepoStatusStore status,
        IMessageBus bus,
        IGitService git,
        IPlatformShell? shell,
        RepoNodeFactory factory)
    {
        _initial = repo;
        Depth = depth;
        _registry = registry;
        _status = status;
        _bus = bus;
        _git = git;
        _shell = shell;

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

        _childRepos = new Derived<IReadOnlyList<Repo>>(ComputeChildRepos);
        _children = new KeyedViewModelList<Repo, Guid, RepoNodeViewModel>(
            _childRepos, r => r.Id, r => factory.Create(r, depth + 1));

        ToggleExpand = new Command(() => _registry.SetWorktreeExpanded(RepoId, !IsExpanded.Value), HasChildren);
    }

    public void Activate() => _registry.SetActive(RepoId);

    // A submodule with no .git of its own has nothing to navigate to; worktrees and primaries
    // always activate.
    public bool CanActivate() => Kind != RepoKind.Submodule || !IsMissing.Value;

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
        var sourceGroup = _registry.FindGroupContaining(repo.Id);
        var items = new List<RepoBarContextMenu.Item>
        {
            new("New worktree…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new CreateWorktreeDialog { Primary = repo, OnClose = onClose })),
                LucideIcons.Branch),
            new("Prune worktrees",
                () =>
                {
                    Task.Run(() => _git.PruneWorktrees(repo));
                    _bus.Broadcast(new WorktreesChangedMessage(repo.Id));
                },
                LucideIcons.Trash),
            new("Add submodule…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new AddSubmoduleDialog { Primary = repo, OnClose = onClose })),
                LucideIcons.Package),
            new("Update all submodules…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new UpdateSubmodulesDialog { Primary = repo, Target = null, OnClose = onClose })),
                LucideIcons.Pull),
        };

        foreach (var group in _registry.Groups)
        {
            if (sourceGroup != null && group.Id == sourceGroup.Id) continue;
            var captured = group;
            items.Add(new RepoBarContextMenu.Item(
                $"Move to: {captured.Name.Value}",
                () => _registry.MoveRepo(repo.Id, captured.Id, captured.RepoIds.Count),
                LucideIcons.FolderInput));
        }

        items.Add(new RepoBarContextMenu.Item("Remove repo", () => _registry.RemoveRepo(repo.Id), LucideIcons.Trash));
        items.Add(new RepoBarContextMenu.Item(
            "New group",
            () =>
            {
                var id = _registry.CreateGroup("New Group");
                _registry.BeginRenameGroup(id);
            },
            LucideIcons.FolderPlus));

        return items;
    }

    private IReadOnlyList<RepoBarContextMenu.Item> BuildWorktreeMenu()
    {
        var worktree = Repo;
        var items = new List<RepoBarContextMenu.Item>
        {
            new("Switch to worktree", () => _registry.SetActive(worktree.Id), LucideIcons.Branch),
        };

        if (_shell is not null)
            items.Add(new RepoBarContextMenu.Item("Open folder", () => _shell.OpenFolder(worktree.Path), LucideIcons.FolderOpen));

        if (worktree.ParentRepoId is { } parentId && FindRepo(parentId) is { } primary)
        {
            items.Add(new RepoBarContextMenu.Item(
                "Remove worktree…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new RemoveWorktreeDialog { Primary = primary, Worktree = worktree, OnClose = onClose })),
                LucideIcons.Trash));
        }

        return items;
    }

    private IReadOnlyList<RepoBarContextMenu.Item> BuildSubmoduleMenu()
    {
        var submodule = Repo;
        var items = new List<RepoBarContextMenu.Item>();

        if (!submodule.IsMissing)
            items.Add(new RepoBarContextMenu.Item("Switch to submodule", () => _registry.SetActive(submodule.Id), LucideIcons.Package));

        if (_shell is not null)
            items.Add(new RepoBarContextMenu.Item("Open folder", () => _shell.OpenFolder(submodule.Path), LucideIcons.FolderOpen));

        if (submodule.ParentRepoId is { } parentId && FindRepo(parentId) is { } primary)
        {
            items.Add(new RepoBarContextMenu.Item(
                "Update submodule…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new UpdateSubmodulesDialog { Primary = primary, Target = submodule, OnClose = onClose })),
                LucideIcons.Pull));
            items.Add(new RepoBarContextMenu.Item(
                "Deinit submodule…",
                () => _bus.Broadcast(new ShowDialogMessage(onClose => new DeinitSubmoduleDialog { Primary = primary, Submodule = submodule, OnClose = onClose })),
                LucideIcons.Trash));
        }

        return items;
    }

    public void Dispose()
    {
        _children.Dispose();
        _childRepos.Dispose();
        _hasChildren.Dispose();
        _badge.Dispose();
        _isActive.Dispose();
        _isMissing.Dispose();
        _displayName.Dispose();
        _currentRepo.Dispose();
    }
}
