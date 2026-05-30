using ZGF.Observable;

namespace GitGui;

public sealed class RepoRegistry : IRepoRegistry
{
    private const string DefaultNewGroupName = "New Group";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _statePath;
    private readonly Dictionary<Guid, BranchesUiState> _branchesUi;
    private readonly Dictionary<Guid, bool> _worktreesExpanded;

    public RepoRegistry(RepoStateStore.State initial, string statePath)
    {
        _statePath = statePath;
        _branchesUi = new Dictionary<Guid, BranchesUiState>(initial.BranchesUi);
        _worktreesExpanded = new Dictionary<Guid, bool>(initial.WorktreesExpanded);

        Repos = new ObservableList<Repo>();
        foreach (var r in initial.Repos) Repos.Add(r);

        Groups = new ObservableList<Group>();
        foreach (var g in initial.Groups) Groups.Add(g);

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

    public void Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var normalized = Path.GetFullPath(path);
        if (!RepoStateStore.IsGitRepo(normalized))
            return;

        var existing = Repos.FirstOrDefault(r => string.Equals(r.Path, normalized, PathComparison));
        if (existing is not null)
        {
            SetActive(existing.Id);
            return;
        }

        var repo = new Repo(Guid.NewGuid(), normalized, Path.GetFileName(normalized));
        Repos.Add(repo);

        var first = Groups[0];
        Groups.Replace(0, first with { RepoIds = first.RepoIds.Append(repo.Id).ToList() });

        Active.Value = repo;
        Save();
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
        for (var i = 0; i < Groups.Count; i++)
        {
            if (Groups[i].Id != groupId) continue;
            Groups.Replace(i, Groups[i] with { IsCollapsed = !Groups[i].IsCollapsed });
            Save();
            return;
        }
    }

    public Guid CreateGroup(string name)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? DefaultNewGroupName : name;
        var group = new Group(Guid.NewGuid(), displayName, IsCollapsed: false, RepoIds: new List<Guid>());
        Groups.Add(group);
        Save();
        return group.Id;
    }

    public void RenameGroup(Guid id, string newName)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        for (var i = 0; i < Groups.Count; i++)
        {
            if (Groups[i].Id != id) continue;
            if (Groups[i].Name == trimmed) return;
            Groups.Replace(i, Groups[i] with { Name = trimmed });
            Save();
            return;
        }
    }

    public void DeleteGroup(Guid id)
    {
        if (Groups.Count <= 1) return;

        var index = -1;
        for (var i = 0; i < Groups.Count; i++)
        {
            if (Groups[i].Id != id) continue;
            index = i;
            break;
        }
        if (index < 0) return;

        var orphans = Groups[index].RepoIds.ToList();
        Groups.RemoveAt(index);

        if (orphans.Count > 0)
        {
            var targetIndex = index < Groups.Count ? index : Groups.Count - 1;
            var target = Groups[targetIndex];
            Groups.Replace(targetIndex, target with { RepoIds = target.RepoIds.Concat(orphans).ToList() });
        }
        Save();
    }

    public void MoveRepo(Guid repoId, Guid targetGroupId, int insertIndex)
    {
        // Worktrees and submodules follow their primary; only primaries appear in groups.
        var repo = Repos.FirstOrDefault(r => r.Id == repoId);
        if (repo is not null && !repo.IsPrimary) return;

        int sourceGroupIndex = -1;
        int sourceRepoIndex = -1;
        for (var i = 0; i < Groups.Count; i++)
        {
            var repoIdx = Groups[i].RepoIds.IndexOf(repoId);
            if (repoIdx < 0) continue;
            sourceGroupIndex = i;
            sourceRepoIndex = repoIdx;
            break;
        }
        if (sourceGroupIndex < 0) return;

        var targetGroupIndex = -1;
        for (var i = 0; i < Groups.Count; i++)
        {
            if (Groups[i].Id != targetGroupId) continue;
            targetGroupIndex = i;
            break;
        }
        if (targetGroupIndex < 0) return;

        if (sourceGroupIndex == targetGroupIndex)
        {
            var ids = Groups[sourceGroupIndex].RepoIds.ToList();
            ids.RemoveAt(sourceRepoIndex);
            var adjusted = insertIndex > sourceRepoIndex ? insertIndex - 1 : insertIndex;
            adjusted = Math.Clamp(adjusted, 0, ids.Count);
            ids.Insert(adjusted, repoId);
            if (ids.SequenceEqual(Groups[sourceGroupIndex].RepoIds)) return;
            Groups.Replace(sourceGroupIndex, Groups[sourceGroupIndex] with { RepoIds = ids });
            Save();
            return;
        }

        var sourceIds = Groups[sourceGroupIndex].RepoIds.ToList();
        sourceIds.RemoveAt(sourceRepoIndex);
        Groups.Replace(sourceGroupIndex, Groups[sourceGroupIndex] with { RepoIds = sourceIds });

        var targetIds = Groups[targetGroupIndex].RepoIds.ToList();
        var insertAt = Math.Clamp(insertIndex, 0, targetIds.Count);
        targetIds.Insert(insertAt, repoId);
        Groups.Replace(targetGroupIndex, Groups[targetGroupIndex] with { RepoIds = targetIds });

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

        for (var i = 0; i < Groups.Count; i++)
        {
            if (!Groups[i].RepoIds.Any(idsToRemove.Contains)) continue;
            var ids = Groups[i].RepoIds.Where(id => !idsToRemove.Contains(id)).ToList();
            Groups.Replace(i, Groups[i] with { RepoIds = ids });
        }

        for (var i = Repos.Count - 1; i >= 0; i--)
        {
            if (idsToRemove.Contains(Repos[i].Id)) Repos.RemoveAt(i);
        }

        foreach (var id in idsToRemove) _worktreesExpanded.Remove(id);

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

    public bool IsWorktreeExpanded(Guid primaryId)
        => !_worktreesExpanded.TryGetValue(primaryId, out var v) || v;

    public void SetWorktreeExpanded(Guid primaryId, bool expanded)
    {
        if (IsWorktreeExpanded(primaryId) == expanded) return;
        _worktreesExpanded[primaryId] = expanded;
        WorktreesChanged.Value++;
        Save();
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
            PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var changed = false;
        var seenPaths = new HashSet<string>(PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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

        // Remove worktree rows that no longer appear in `git worktree list`.
        for (var i = Repos.Count - 1; i >= 0; i--)
        {
            var r = Repos[i];
            if (r.ParentRepoId != primaryId || !r.IsWorktree) continue;
            var normalized = Path.GetFullPath(r.Path);
            if (!desiredByPath.ContainsKey(normalized))
            {
                Repos.RemoveAt(i);
                _worktreesExpanded.Remove(r.Id);
                if (Active.Value?.Id == r.Id) Active.Value = primary;
                changed = true;
            }
        }

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
    // ReplaceWorktreesFor but only touches submodule children. Submodule rows survive
    // path-stable syncs so their Repo.Id stays put and Active doesn't get invalidated
    // when a watcher re-runs discovery.
    public void ReplaceSubmodulesFor(Guid primaryId, IReadOnlyList<SubmoduleDescriptor> desired)
    {
        var primary = Repos.FirstOrDefault(r => r.Id == primaryId);
        if (primary is null || !primary.IsPrimary) return;

        var pathComparer = PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var desiredByPath = new Dictionary<string, SubmoduleDescriptor>(pathComparer);
        foreach (var d in desired) desiredByPath[Path.GetFullPath(d.Path)] = d;

        var changed = false;
        var seenPaths = new HashSet<string>(pathComparer);

        for (var i = 0; i < Repos.Count; i++)
        {
            var r = Repos[i];
            if (r.ParentRepoId != primaryId || !r.IsSubmodule) continue;
            var normalized = Path.GetFullPath(r.Path);
            seenPaths.Add(normalized);
            if (desiredByPath.TryGetValue(normalized, out var d))
            {
                var newDisplay = d.DisplayName ?? Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (r.DisplayName != newDisplay || r.Branch != d.Branch || r.IsMissing != !RepoStateStore.IsGitRepo(r.Path))
                {
                    Repos.Replace(i, r with { DisplayName = newDisplay, Branch = d.Branch, IsMissing = !RepoStateStore.IsGitRepo(r.Path) });
                    changed = true;
                }
            }
        }

        for (var i = Repos.Count - 1; i >= 0; i--)
        {
            var r = Repos[i];
            if (r.ParentRepoId != primaryId || !r.IsSubmodule) continue;
            var normalized = Path.GetFullPath(r.Path);
            if (!desiredByPath.ContainsKey(normalized))
            {
                Repos.RemoveAt(i);
                _worktreesExpanded.Remove(r.Id);
                if (Active.Value?.Id == r.Id) Active.Value = primary;
                changed = true;
            }
        }

        foreach (var d in desired)
        {
            var normalized = Path.GetFullPath(d.Path);
            if (seenPaths.Contains(normalized)) continue;
            var display = d.DisplayName ?? Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Repos.Add(new Repo(Guid.NewGuid(), normalized, display, ParentRepoId: primaryId)
            {
                Branch = d.Branch,
                Kind = RepoKind.Submodule,
                IsMissing = !RepoStateStore.IsGitRepo(normalized),
            });
            changed = true;
        }

        if (changed)
        {
            WorktreesChanged.Value++;
            Save();
        }
    }

    private void Save() =>
        RepoStateStore.Save(_statePath, Repos, Groups, Active.Value?.Id, _branchesUi, _worktreesExpanded);
}

public sealed record WorktreeDescriptor(string Path, string? DisplayName, string? Branch = null);

public sealed record SubmoduleDescriptor(string Path, string? DisplayName, string? Branch = null);
