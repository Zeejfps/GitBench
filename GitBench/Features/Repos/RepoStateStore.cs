using System.Text.Json;
using System.Text.Json.Serialization;
using GitBench.Features.Branches;
using GitBench.Git;
using GitBench.Infrastructure;

namespace GitBench.Features.Repos;

public static class RepoStateStore
{
    private const int CurrentSchemaVersion = 6;
    private const string DefaultGroupName = "Ungrouped";
    // Pre-v5 default group name; renamed on load so it no longer duplicates the sidebar's panel title.
    private const string LegacyDefaultGroupName = "Repositories";

    public sealed record State(
        List<Repo> Repos,
        List<GroupState> Groups,
        Guid? ActiveRepoId,
        Dictionary<Guid, BranchesUiState> BranchesUi,
        Dictionary<Guid, bool> WorktreesExpanded,
        Dictionary<Guid, Guid> RepoIdentityOverride,
        Dictionary<int, Guid> Hotkeys);

    internal sealed class FileShape
    {
        public int SchemaVersion { get; set; }
        public List<Repo> Repos { get; set; } = new();
        public List<GroupState>? Groups { get; set; }
        public Guid? ActiveRepoId { get; set; }
        public Dictionary<Guid, BranchesUiState>? BranchesUi { get; set; }
        public Dictionary<Guid, bool>? WorktreesExpanded { get; set; }
        // repoId → identity profile id (manual override of auto-matching). Absent in pre-v4 files.
        public Dictionary<Guid, Guid>? RepoIdentityOverride { get; set; }
        // slot (1-9) → repo id for keyboard repo switching. Absent in pre-v6 files.
        public Dictionary<int, Guid>? Hotkeys { get; set; }
    }

    public static State Load(string path)
    {
        if (!File.Exists(path))
            return EmptyState();

        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize(stream, RepoStateJsonContext.Default.FileShape);
            if (file is null)
                return EmptyState();

            var repos = Enumerable
                .Select<Repo, Repo>(file.Repos, r =>
                {
                    // Backward compat: pre-submodule state files have no Kind field, so the
                    // deserializer defaults Kind = Primary. Migrate by inferring Kind from
                    // ParentRepoId (every existing child was a worktree at the time).
                    var migrated = r;
                    if (r.ParentRepoId is not null && r.Kind == RepoKind.Primary)
                        migrated = r with { Kind = RepoKind.Worktree };
                    return migrated with { IsMissing = !IsGitRepo(migrated.Path) };
                })
                .ToList();

            // Keep only records reachable from a top-level repo through the parent chain — a
            // child whose parent (or any ancestor) is gone can't be reattached and a dangling
            // ParentRepoId would corrupt nested rendering. Nested submodules have a submodule
            // parent, so a single "parent is top-level" check isn't enough: grow the kept set
            // transitively from the roots outward.
            var kept = repos.Where(r => r.ParentRepoId is null).Select(r => r.Id).ToHashSet();
            bool grew;
            do
            {
                grew = false;
                foreach (var r in repos)
                {
                    if (kept.Contains(r.Id)) continue;
                    if (r.ParentRepoId is { } pid && kept.Contains(pid))
                        grew = kept.Add(r.Id) || grew;
                }
            } while (grew);
            repos = repos.Where(r => kept.Contains(r.Id)).ToList();

            var groups = file.Groups;
            if (groups is null || groups.Count == 0)
            {
                groups =
                [
                    new GroupState(Guid.NewGuid(), DefaultGroupName, IsCollapsed: false,
                        RepoIds: repos.Where(r => r.ParentRepoId is null).Select(r => r.Id).ToList())

                ];
            }
            else
            {
                groups = ReconcileGroups(groups, repos);
                if (file.SchemaVersion < 5)
                    groups = groups
                        .Select(g => g.Name == LegacyDefaultGroupName ? g with { Name = DefaultGroupName } : g)
                        .ToList();
            }

            // Keep only slots 1-9 that still point at a live primary, and at most one slot per repo
            // (lowest slot wins), so the 1:1 invariant survives a removed repo or a hand-edited file.
            var primaryIds = repos.Where(r => r.ParentRepoId is null).Select(r => r.Id).ToHashSet();
            var hotkeys = new Dictionary<int, Guid>();
            if (file.Hotkeys is { } storedHotkeys)
            {
                var claimed = new HashSet<Guid>();
                foreach (var (slot, id) in storedHotkeys.OrderBy(kv => kv.Key))
                {
                    if (slot is < 1 or > 9) continue;
                    if (!primaryIds.Contains(id)) continue;
                    if (!claimed.Add(id)) continue;
                    hotkeys[slot] = id;
                }
            }

            return new State(
                repos,
                groups,
                file.ActiveRepoId,
                file.BranchesUi ?? new Dictionary<Guid, BranchesUiState>(),
                file.WorktreesExpanded ?? new Dictionary<Guid, bool>(),
                file.RepoIdentityOverride ?? new Dictionary<Guid, Guid>(),
                hotkeys);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load repo state from {path}: {ex.Message}");
            return EmptyState();
        }
    }

    public static void Save(
        string path,
        IReadOnlyList<Repo> repos,
        IReadOnlyList<GroupState> groups,
        Guid? activeId,
        IReadOnlyDictionary<Guid, BranchesUiState> branchesUi,
        IReadOnlyDictionary<Guid, bool> worktreesExpanded,
        IReadOnlyDictionary<Guid, Guid> repoIdentityOverride,
        IReadOnlyDictionary<int, Guid> hotkeys)
        => AtomicFile.WriteAllText(path,
            Serialize(repos, groups, activeId, branchesUi, worktreesExpanded, repoIdentityOverride, hotkeys));

    // Snapshots the live model into the on-disk shape and serializes it. Runs on the caller's thread
    // (it reads the mutable model, so it must), producing an immutable string the disk write can take
    // off-thread — see BackgroundFileWriter.
    public static string Serialize(
        IReadOnlyList<Repo> repos,
        IReadOnlyList<GroupState> groups,
        Guid? activeId,
        IReadOnlyDictionary<Guid, BranchesUiState> branchesUi,
        IReadOnlyDictionary<Guid, bool> worktreesExpanded,
        IReadOnlyDictionary<Guid, Guid> repoIdentityOverride,
        IReadOnlyDictionary<int, Guid> hotkeys)
    {
        var file = new FileShape
        {
            SchemaVersion = CurrentSchemaVersion,
            Repos = repos.ToList(),
            Groups = groups.ToList(),
            ActiveRepoId = activeId,
            BranchesUi = branchesUi.ToDictionary(kv => kv.Key, kv => kv.Value),
            WorktreesExpanded = worktreesExpanded.ToDictionary(kv => kv.Key, kv => kv.Value),
            RepoIdentityOverride = repoIdentityOverride.ToDictionary(kv => kv.Key, kv => kv.Value),
            Hotkeys = hotkeys.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        return JsonSerializer.Serialize(file, RepoStateJsonContext.Default.FileShape);
    }

    public static bool IsGitRepo(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) ||
        File.Exists(Path.Combine(path, ".git"));

    private static State EmptyState()
    {
        var defaultGroup = new GroupState(Guid.NewGuid(), DefaultGroupName, IsCollapsed: false, RepoIds: new List<Guid>());
        return new State(
            new List<Repo>(),
            new List<GroupState> { defaultGroup },
            null,
            new Dictionary<Guid, BranchesUiState>(),
            new Dictionary<Guid, bool>(),
            new Dictionary<Guid, Guid>(),
            new Dictionary<int, Guid>());
    }

    // Worktrees are children of a primary repo and never appear directly in a Group —
    // they're discovered by ParentRepoId at render time. Filter them out of every
    // RepoIds set defensively, in case a hand-edit or older state file slipped one in.
    private static List<GroupState> ReconcileGroups(List<GroupState> groups, List<Repo> repos)
    {
        var primaries = repos.Where(r => r.ParentRepoId is null).ToList();
        var primaryIds = primaries.Select(r => r.Id).ToHashSet();
        var assigned = groups.SelectMany(g => g.RepoIds).Where(primaryIds.Contains).ToHashSet();
        var orphans = primaries.Select(r => r.Id).Where(id => !assigned.Contains(id)).ToList();

        var cleaned = groups
            .Select(g => g with { RepoIds = g.RepoIds.Where(primaryIds.Contains).ToList() })
            .ToList();

        if (orphans.Count > 0)
        {
            var first = cleaned[0];
            cleaned[0] = first with { RepoIds = first.RepoIds.Concat(orphans).ToList() };
        }
        return cleaned;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RepoStateStore.FileShape))]
internal partial class RepoStateJsonContext : JsonSerializerContext;
