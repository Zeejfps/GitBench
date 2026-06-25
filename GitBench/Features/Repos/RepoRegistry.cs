using GitBench.Features.Branches;
using GitBench.Features.Identity;
using GitBench.Git;
using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.Repos;

public sealed class RepoRegistry : IRepoRegistry, IIdentityOverrides, IDisposable
{
    private const string DefaultNewGroupName = "New Group";

    private readonly string _statePath;
    private readonly BackgroundFileWriter _writer;
    private readonly Dictionary<Guid, BranchesUiState> _branchesUi;
    private readonly Dictionary<Guid, State<bool>> _expanded;
    private readonly Dictionary<Guid, Guid> _identityOverride;

    // Immutable lookups the resolver reads lock-free from background git threads (path→profile-id
    // override, and repo-id→path for targeted memo flushing). Rebuilt on the UI thread (in Save)
    // only when the inputs actually change — _overrideMapDirty gates the rebuild so the ~18 Save()
    // sites that touch neither Repos nor overrides don't pay an O(repos) rebuild + allocation.
    private volatile IReadOnlyDictionary<string, Guid> _overrideByPath =
        new Dictionary<string, Guid>(PathKey.Comparer);
    private volatile IReadOnlyDictionary<Guid, string> _pathById = new Dictionary<Guid, string>();
    private bool _overrideMapDirty;

    public RepoRegistry(RepoStateStore.State initial, string statePath)
    {
        _statePath = statePath;
        _writer = new BackgroundFileWriter(statePath);
        _branchesUi = new Dictionary<Guid, BranchesUiState>(initial.BranchesUi);
        _expanded = new Dictionary<Guid, State<bool>>();
        foreach (var (repoId, expanded) in initial.WorktreesExpanded) _expanded[repoId] = new State<bool>(expanded);
        _identityOverride = new Dictionary<Guid, Guid>(initial.RepoIdentityOverride);

        Repos = new ObservableList<Repo>();
        foreach (var r in initial.Repos) Repos.Add(r);

        RebuildLookups();
        // Any add/remove/replace of a repo row can change a path or parent link the lookups depend
        // on, so mark them dirty; the next Save rebuilds. (Subscribed after the initial seed above.)
        Repos.Changed += _ => _overrideMapDirty = true;

        Groups = new ObservableList<Group>();
        foreach (var g in initial.Groups) Groups.Add(Group.FromState(g));

        Active = new State<Repo?>(
            initial.ActiveRepoId is { } id
                ? Repos.FirstOrDefault(r => r.Id == id)
                : null);

        RenamingGroupId = new State<Guid?>(null);
        WorktreesChanged = new State<int>(0);
    }

    public ObservableList<Repo> Repos { get; }
    public ObservableList<Group> Groups { get; }
    public State<Repo?> Active { get; }
    public State<Guid?> RenamingGroupId { get; }
    public State<int> WorktreesChanged { get; }

    public OpenRepoOutcome Open(string path, Guid? groupId = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return OpenRepoOutcome.NotAGitRepo;

        var normalized = Path.GetFullPath(path);
        if (!RepoStateStore.IsGitRepo(normalized))
            return OpenRepoOutcome.NotAGitRepo;

        var existing = Repos.FirstOrDefault(r => PathKey.Comparer.Equals(r.Path, normalized));
        if (existing is not null)
        {
            SetActive(existing.Id);
            return OpenRepoOutcome.AlreadyOpen;
        }

        var repo = new Repo(Guid.NewGuid(), normalized, Path.GetFileName(normalized));
        Repos.Add(repo);

        var target = (groupId is { } gid ? FindGroup(gid) : null) ?? Groups[0];
        target.RepoIds.Add(repo.Id);

        Active.Value = repo;
        Save();
        return OpenRepoOutcome.Opened;
    }

    public void SetActive(Guid id)
    {
        var target = Repos.FirstOrDefault(r => r.Id == id);
        if (target is null) return;
        if (ReferenceEquals(Active.Value, target)) return;
        Active.Value = target;
        Save();
    }

    public void ToggleGroupCollapsed(Guid groupId)
    {
        if (FindGroup(groupId) is not { } group) return;
        group.IsCollapsed.Value = !group.IsCollapsed.Value;
        Save();
    }

    public void SetAllGroupsCollapsed(bool collapsed)
    {
        var changed = false;
        foreach (var group in Groups)
        {
            if (group.IsCollapsed.Value == collapsed) continue;
            group.IsCollapsed.Value = collapsed;
            changed = true;
        }
        if (changed) Save();
    }

    public Guid CreateGroup(string name)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? DefaultNewGroupName : name;
        var group = new Group(Guid.NewGuid(), displayName, isCollapsed: false, repoIds: new List<Guid>());
        Groups.Add(group);
        Save();
        return group.Id;
    }

    public void RenameGroup(Guid id, string newName)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        if (FindGroup(id) is not { } group || group.Name.Value == trimmed) return;
        group.Name.Value = trimmed;
        Save();
    }

    public void DeleteGroup(Guid id)
    {
        if (Groups.Count <= 1) return;

        var index = IndexOfGroup(id);
        if (index < 0) return;

        var orphans = Groups[index].RepoIds.ToList();
        Groups.RemoveAt(index);

        if (orphans.Count > 0)
        {
            var target = Groups[index < Groups.Count ? index : Groups.Count - 1];
            foreach (var repoId in orphans) target.RepoIds.Add(repoId);
        }
        Save();
    }

    public void MoveRepo(Guid repoId, Guid targetGroupId, int insertIndex)
    {
        // Worktrees and submodules follow their primary; only primaries appear in groups.
        var repo = Repos.FirstOrDefault(r => r.Id == repoId);
        if (repo is not null && !repo.IsPrimary) return;

        Group? source = null;
        var sourceRepoIndex = -1;
        foreach (var g in Groups)
        {
            var repoIdx = g.RepoIds.IndexOf(repoId);
            if (repoIdx < 0) continue;
            source = g;
            sourceRepoIndex = repoIdx;
            break;
        }
        if (source is null) return;

        var target = FindGroup(targetGroupId);
        if (target is null) return;

        if (ReferenceEquals(source, target))
        {
            var adjusted = insertIndex > sourceRepoIndex ? insertIndex - 1 : insertIndex;
            adjusted = Math.Clamp(adjusted, 0, source.RepoIds.Count - 1);
            if (adjusted == sourceRepoIndex) return;
            source.RepoIds.Move(sourceRepoIndex, adjusted);
            Save();
            return;
        }

        source.RepoIds.RemoveAt(sourceRepoIndex);
        var insertAt = Math.Clamp(insertIndex, 0, target.RepoIds.Count);
        target.RepoIds.Insert(insertAt, repoId);
        Save();
    }

    public void MoveGroup(Guid groupId, int insertIndex)
    {
        var sourceIndex = -1;
        for (var i = 0; i < Groups.Count; i++)
        {
            if (Groups[i].Id != groupId) continue;
            sourceIndex = i;
            break;
        }
        if (sourceIndex < 0) return;

        var adjusted = insertIndex > sourceIndex ? insertIndex - 1 : insertIndex;
        adjusted = Math.Clamp(adjusted, 0, Groups.Count - 1);
        if (adjusted == sourceIndex) return;

        Groups.Move(sourceIndex, adjusted);
        Save();
    }

    public void RemoveRepo(Guid repoId)
    {
        var repoIndex = -1;
        for (var i = 0; i < Repos.Count; i++)
        {
            if (Repos[i].Id != repoId) continue;
            repoIndex = i;
            break;
        }
        if (repoIndex < 0) return;

        var target = Repos[repoIndex];

        // Removing a primary cascades to its child rows (worktrees + submodules) — they
        // have no meaning without a parent registry entry.
        var idsToRemove = new HashSet<Guid> { repoId };
        if (target.IsPrimary)
        {
            foreach (var r in Repos)
            {
                if (r.ParentRepoId == repoId) idsToRemove.Add(r.Id);
            }
        }

        foreach (var group in Groups)
        {
            for (var k = group.RepoIds.Count - 1; k >= 0; k--)
            {
                if (idsToRemove.Contains(group.RepoIds[k]))
                    group.RepoIds.RemoveAt(k);
            }
        }

        for (var i = Repos.Count - 1; i >= 0; i--)
        {
            if (idsToRemove.Contains(Repos[i].Id)) Repos.RemoveAt(i);
        }

        foreach (var id in idsToRemove) RemoveExpandedState(id);

        if (Active.Value is { } active && idsToRemove.Contains(active.Id))
        {
            Active.Value = Repos.Count > 0 ? Repos[0] : null;
        }

        Save();
    }

    public void BeginRenameGroup(Guid id)
    {
        RenamingGroupId.Value = id;
    }

    public void EndRenameGroup()
    {
        RenamingGroupId.Value = null;
    }

    public BranchesUiState GetBranchesUi(Guid repoId)
    {
        if (_branchesUi.TryGetValue(repoId, out var state))
            return state.Clone();
        return new BranchesUiState();
    }

    public void SetBranchesUi(Guid repoId, BranchesUiState state)
    {
        _branchesUi[repoId] = state.Clone();
        Save();
    }

    public IEnumerable<Repo> GetWorktrees(Guid primaryId)
    {
        foreach (var r in Repos)
        {
            if (r.ParentRepoId == primaryId && r.IsWorktree) yield return r;
        }
    }

    public IEnumerable<Repo> GetSubmodules(Guid primaryId)
    {
        foreach (var r in Repos)
        {
            if (r.ParentRepoId == primaryId && r.IsSubmodule) yield return r;
        }
    }

    public IReadable<bool> WatchWorktreeExpanded(Guid primaryId) => ExpandedState(primaryId);

    public void SetWorktreeExpanded(Guid primaryId, bool expanded)
    {
        var state = ExpandedState(primaryId);
        if (state.Value == expanded) return;
        state.Value = expanded;
        Save();
    }

    // The expand flag for one row's fold chevron, created lazily and defaulting to expanded.
    // A live State (rather than a dict entry guarded by a global counter) so the chevron and
    // the child-row lists bind to it directly.
    private State<bool> ExpandedState(Guid primaryId)
    {
        if (!_expanded.TryGetValue(primaryId, out var state))
        {
            state = new State<bool>(true);
            _expanded[primaryId] = state;
        }
        return state;
    }

    private void RemoveExpandedState(Guid primaryId)
    {
        if (_expanded.Remove(primaryId, out var state)) state.Dispose();
    }

    public Guid? GetIdentityOverride(Guid repoId)
        => _identityOverride.TryGetValue(repoId, out var id) ? id : null;

    public void SetIdentityOverride(Guid repoId, Guid? profileId)
    {
        if (profileId is { } id) _identityOverride[repoId] = id;
        else _identityOverride.Remove(repoId);
        _overrideMapDirty = true;
        Save();
    }

    // Resolver-facing lookup: the resolution memo is keyed by working-dir path, so we serve an
    // O(1) read from the precomputed map (built on the UI thread). Keys use PathKey, the same
    // normalization the resolver applies to its memo key, so the two can't drift.
    public Guid? GetIdentityOverrideByPath(string path)
        => _overrideByPath.TryGetValue(PathKey.Normalize(path), out var id) ? id : null;

    // Repo-id → working-dir path, served lock-free for the resolver's targeted RefsChanged flush.
    public string? GetRepoPathById(Guid repoId)
        => _pathById.TryGetValue(repoId, out var path) ? path : null;

    // The override that applies to a repo: its own pin, else the nearest pinned ancestor's. Walks
    // the whole parent chain so a submodule-of-a-submodule still inherits a top-level pin; a child
    // can carry its own pin to opt out. The depth guard stops a cyclic ParentRepoId looping forever.
    private Guid? EffectiveOverride(Repo r, IReadOnlyDictionary<Guid, Repo> byId)
    {
        var cur = r;
        for (var depth = 0; cur != null && depth < 64; depth++)
        {
            if (_identityOverride.TryGetValue(cur.Id, out var id)) return id;
            cur = cur.ParentRepoId is { } pid && byId.TryGetValue(pid, out var parent) ? parent : null;
        }
        return null;
    }

    private void RebuildLookups()
    {
        var byId = new Dictionary<Guid, Repo>();
        foreach (var r in Repos) byId[r.Id] = r;

        var overrideMap = new Dictionary<string, Guid>(PathKey.Comparer);
        var pathById = new Dictionary<Guid, string>();
        foreach (var r in Repos)
        {
            pathById[r.Id] = r.Path;
            if (EffectiveOverride(r, byId) is { } id)
                overrideMap[PathKey.Normalize(r.Path)] = id;
        }
        _overrideByPath = overrideMap;
        _pathById = pathById;
    }

    // Records the primary's HEAD branch (from `git worktree list`) so the BranchesView
    // can mark it as "taken" when active is a sibling worktree.
    public void SetPrimaryBranch(Guid primaryId, string? branch)
    {
        for (var i = 0; i < Repos.Count; i++)
        {
            var r = Repos[i];
            if (r.Id != primaryId || !r.IsPrimary) continue;
            if (r.Branch == branch) return;
            Repos.Replace(i, r with { Branch = branch });
            WorktreesChanged.Value++;
            Save();
            return;
        }
    }

    // Discovery side-effect entry point: callers pass the result of `git worktree list`
    // (with the primary's own entry already filtered out). The registry diffs against
    // its current worktree children, adding/removing Repo records and migrating Active if
    // the user is sitting on a worktree that just disappeared on disk.
    public void ReplaceWorktreesFor(Guid primaryId, IReadOnlyList<WorktreeDescriptor> desired)
    {
        var primary = Repos.FirstOrDefault(r => r.Id == primaryId);
        if (primary is null || !primary.IsPrimary) return;

        var desiredByPath = new Dictionary<string, WorktreeDescriptor>(
            desired.ToDictionary(d => Path.GetFullPath(d.Path), d => d),
            PathKey.Comparer);

        var changed = false;
        var seenPaths = new HashSet<string>(PathKey.Comparer);

        // Update or keep existing worktree rows.
        for (var i = 0; i < Repos.Count; i++)
        {
            var r = Repos[i];
            if (r.ParentRepoId != primaryId || !r.IsWorktree) continue;
            var normalized = Path.GetFullPath(r.Path);
            seenPaths.Add(normalized);
            if (desiredByPath.TryGetValue(normalized, out var d))
            {
                var newDisplay = d.DisplayName ?? Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (r.DisplayName != newDisplay || r.Branch != d.Branch || r.IsMissing)
                {
                    Repos.Replace(i, r with { DisplayName = newDisplay, Branch = d.Branch, IsMissing = false });
                    changed = true;
                }
            }
        }

        var worktreesToRemove = new List<Guid>();
        for (var i = 0; i < Repos.Count; i++)
        {
            var r = Repos[i];
            if (r.ParentRepoId != primaryId || !r.IsWorktree) continue;
            if (!desiredByPath.ContainsKey(Path.GetFullPath(r.Path)))
                worktreesToRemove.Add(r.Id);
        }
        foreach (var id in worktreesToRemove)
            changed |= RemoveRepoSubtree(id, primary);

        // Add newly discovered worktrees.
        foreach (var d in desired)
        {
            var normalized = Path.GetFullPath(d.Path);
            if (seenPaths.Contains(normalized)) continue;
            var display = d.DisplayName ?? Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Repos.Add(new Repo(Guid.NewGuid(), normalized, display, ParentRepoId: primaryId) { Branch = d.Branch, Kind = RepoKind.Worktree });
            changed = true;
        }

        if (changed)
        {
            WorktreesChanged.Value++;
            Save();
        }
    }

    // Discovery side-effect entry point for submodules — same diff/reconcile shape as
    // ReplaceWorktreesFor but recursive: each node carries its own nested submodules, so the
    // whole tree under the host is reconciled in one pass. The host is a primary or a worktree.
    public void ReplaceSubmoduleForest(Guid hostId, IReadOnlyList<SubmoduleNode> roots)
    {
        var host = Repos.FirstOrDefault(r => r.Id == hostId);
        if (host is null || (!host.IsPrimary && !host.IsWorktree)) return;

        if (ReconcileSubmoduleLevel(hostId, roots, PathKey.Comparer))
        {
            WorktreesChanged.Value++;
            Save();
        }
    }

    // Reconciles the direct submodule children of parentId against `nodes`, then recurses into
    // each node's own children (resolving the child row by path). Returns whether anything in
    // the subtree changed. parentId may be a primary OR a submodule — that's what enables nesting.
    private bool ReconcileSubmoduleLevel(Guid parentId, IReadOnlyList<SubmoduleNode> nodes, StringComparer pathComparer)
    {
        // Removing the active row migrates Active up one level to the parent (which survives this
        // reconcile), rather than jumping all the way to the primary.
        var fallbackActive = Repos.FirstOrDefault(r => r.Id == parentId);

        var desiredByPath = new Dictionary<string, SubmoduleNode>(pathComparer);
        foreach (var n in nodes) desiredByPath[Path.GetFullPath(n.Descriptor.Path)] = n;

        var changed = false;
        var seenPaths = new HashSet<string>(pathComparer);

        for (var i = 0; i < Repos.Count; i++)
        {
            var r = Repos[i];
            if (r.ParentRepoId != parentId || !r.IsSubmodule) continue;
            var normalized = Path.GetFullPath(r.Path);
            seenPaths.Add(normalized);
            if (desiredByPath.TryGetValue(normalized, out var node))
            {
                var d = node.Descriptor;
                var newDisplay = d.DisplayName ?? Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var missing = !RepoStateStore.IsGitRepo(r.Path);
                if (r.DisplayName != newDisplay || r.Branch != d.Branch || r.IsMissing != missing)
                {
                    Repos.Replace(i, r with { DisplayName = newDisplay, Branch = d.Branch, IsMissing = missing });
                    changed = true;
                }
            }
        }

        for (var i = Repos.Count - 1; i >= 0; i--)
        {
            var r = Repos[i];
            if (r.ParentRepoId != parentId || !r.IsSubmodule) continue;
            var normalized = Path.GetFullPath(r.Path);
            if (!desiredByPath.ContainsKey(normalized))
                changed |= RemoveRepoSubtree(r.Id, fallbackActive);
        }

        foreach (var node in nodes)
        {
            var normalized = Path.GetFullPath(node.Descriptor.Path);
            if (seenPaths.Contains(normalized)) continue;
            var d = node.Descriptor;
            var display = d.DisplayName ?? Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Repos.Add(new Repo(Guid.NewGuid(), normalized, display, ParentRepoId: parentId)
            {
                Branch = d.Branch,
                Kind = RepoKind.Submodule,
                IsMissing = !RepoStateStore.IsGitRepo(normalized),
            });
            changed = true;
        }

        // Recurse into each node's nested submodules now that its row is guaranteed present.
        foreach (var node in nodes)
        {
            var normalized = Path.GetFullPath(node.Descriptor.Path);
            Repo? child = null;
            foreach (var r in Repos)
            {
                if (r.ParentRepoId == parentId && r.IsSubmodule &&
                    pathComparer.Equals(Path.GetFullPath(r.Path), normalized))
                {
                    child = r;
                    break;
                }
            }
            if (child is not null)
                changed |= ReconcileSubmoduleLevel(child.Id, node.Children, pathComparer);
        }

        return changed;
    }

    private bool RemoveRepoSubtree(Guid repoId, Repo? fallbackActive)
    {
        var removedAny = false;

        for (var i = Repos.Count - 1; i >= 0; i--)
        {
            if (Repos[i].ParentRepoId == repoId)
                removedAny |= RemoveRepoSubtree(Repos[i].Id, fallbackActive);
        }

        for (var i = 0; i < Repos.Count; i++)
        {
            if (Repos[i].Id != repoId) continue;
            Repos.RemoveAt(i);
            RemoveExpandedState(repoId);
            if (Active.Value?.Id == repoId) Active.Value = fallbackActive;
            removedAny = true;
            break;
        }

        return removedAny;
    }

    private void Save()
    {
        // Only rebuild the resolver's lock-free lookups when a repo row or an override actually
        // changed — most Save() callers (active repo, group edits, branch UI) don't touch either.
        if (_overrideMapDirty)
        {
            RebuildLookups();
            _overrideMapDirty = false;
        }
        // Only collapsed rows carry information — expanded is the default, so an expanded entry
        // is redundant and would bloat the state file with one row per rendered repo.
        var collapsed = _expanded
            .Where(kv => !kv.Value.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        // Serialize here (must read the live model on this thread); hand the finished text to the
        // background writer so the disk write — the slow, UI-thread-stalling part — runs off-thread.
        var json = RepoStateStore.Serialize(Repos, Groups.Select(g => g.ToState()).ToList(),
            Active.Value?.Id, _branchesUi, collapsed, _identityOverride);
        _writer.Schedule(json);
    }

    public void Dispose() => _writer.Dispose();

    private Group? FindGroup(Guid id)
    {
        foreach (var g in Groups)
            if (g.Id == id) return g;
        return null;
    }

    private int IndexOfGroup(Guid id)
    {
        for (var i = 0; i < Groups.Count; i++)
            if (Groups[i].Id == id) return i;
        return -1;
    }
}

public sealed record WorktreeDescriptor(string Path, string? DisplayName, string? Branch = null);

public sealed record SubmoduleDescriptor(string Path, string? DisplayName, string? Branch = null);

// A submodule plus its own nested submodules, forming the tree that ReplaceSubmoduleForest
// reconciles. Children is empty for a leaf (or an uninitialized submodule we didn't recurse into).
public sealed record SubmoduleNode(SubmoduleDescriptor Descriptor, IReadOnlyList<SubmoduleNode> Children);
