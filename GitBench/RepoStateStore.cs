using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitGui;

public static class RepoStateStore
{
    private const int CurrentSchemaVersion = 3;
    private const string DefaultGroupName = "Repositories";

    public sealed record State(
        List<Repo> Repos,
        List<Group> Groups,
        Guid? ActiveRepoId,
        Dictionary<Guid, BranchesUiState> BranchesUi,
        Dictionary<Guid, bool> WorktreesExpanded);

    internal sealed class FileShape
    {
        public int SchemaVersion { get; set; }
        public List<Repo> Repos { get; set; } = new();
        public List<Group>? Groups { get; set; }
        public Guid? ActiveRepoId { get; set; }
        public Dictionary<Guid, BranchesUiState>? BranchesUi { get; set; }
        public Dictionary<Guid, bool>? WorktreesExpanded { get; set; }
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

            var repos = file.Repos
                .Select(r =>
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

            // Drop child records whose parent primary is gone — they can't be reattached
            // and a dangling ParentRepoId would corrupt nested rendering.
            var primaryIds = repos.Where(r => r.ParentRepoId is null).Select(r => r.Id).ToHashSet();
            repos = repos.Where(r => r.ParentRepoId is null || primaryIds.Contains(r.ParentRepoId.Value)).ToList();

            var groups = file.Groups;
            if (groups is null || groups.Count == 0)
            {
                groups =
                [
                    new Group(Guid.NewGuid(), DefaultGroupName, IsCollapsed: false,
                        RepoIds: repos.Where(r => r.ParentRepoId is null).Select(r => r.Id).ToList())

                ];
            }
            else
            {
                groups = ReconcileGroups(groups, repos);
            }

            return new State(
                repos,
                groups,
                file.ActiveRepoId,
                file.BranchesUi ?? new Dictionary<Guid, BranchesUiState>(),
                file.WorktreesExpanded ?? new Dictionary<Guid, bool>());
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
        IReadOnlyList<Group> groups,
        Guid? activeId,
        IReadOnlyDictionary<Guid, BranchesUiState> branchesUi,
        IReadOnlyDictionary<Guid, bool> worktreesExpanded)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var file = new FileShape
        {
            SchemaVersion = CurrentSchemaVersion,
            Repos = repos.ToList(),
            Groups = groups.ToList(),
            ActiveRepoId = activeId,
            BranchesUi = branchesUi.ToDictionary(kv => kv.Key, kv => kv.Value),
            WorktreesExpanded = worktreesExpanded.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        var json = JsonSerializer.Serialize(file, RepoStateJsonContext.Default.FileShape);
        File.WriteAllText(path, json);
    }

    public static bool IsGitRepo(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) ||
        File.Exists(Path.Combine(path, ".git"));

    private static State EmptyState()
    {
        var defaultGroup = new Group(Guid.NewGuid(), DefaultGroupName, IsCollapsed: false, RepoIds: new List<Guid>());
        return new State(
            new List<Repo>(),
            new List<Group> { defaultGroup },
            null,
            new Dictionary<Guid, BranchesUiState>(),
            new Dictionary<Guid, bool>());
    }

    // Worktrees are children of a primary repo and never appear directly in a Group —
    // they're discovered by ParentRepoId at render time. Filter them out of every
    // RepoIds set defensively, in case a hand-edit or older state file slipped one in.
    private static List<Group> ReconcileGroups(List<Group> groups, List<Repo> repos)
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
