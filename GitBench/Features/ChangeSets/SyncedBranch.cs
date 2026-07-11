namespace GitBench.Features.ChangeSets;

// Phase 1's detection unit: a branch name carried by >= 2 primaries within one sidebar group —
// i.e. an implicit cross-repo change set (Locked decision #1, correlation by branch name). RepoIds
// are ordered by the group's membership order and never include a member for which this name is
// that member's own default branch.
public sealed record SyncedBranch(
    string BranchName,
    IReadOnlyList<Guid> RepoIds);

// One primary's inputs to same-name correlation: its own default branch (excluded from sets) and
// its local branch names. Snapshotted off-thread by SyncedBranchIndex from IGitService, or built
// directly from GitService in tests.
public sealed record RepoBranchSnapshot(
    string? DefaultBranch,
    IReadOnlyCollection<string> LocalBranchNames);

// Pure same-name correlation over one group's ordered primaries — no git, no I/O, so the tests can
// drive it with real fixture repos through GitService. A branch forms a change set when >= 2 of the
// group's members carry a local branch of that name; each member's own default branch is excluded
// (detached HEADs excluded implicitly — they have no branch name in LocalBranchNames).
internal static class SyncedBranchCorrelator
{
    public static IReadOnlyList<SyncedBranch> Correlate(
        IReadOnlyList<Guid> orderedRepoIds,
        IReadOnlyDictionary<Guid, RepoBranchSnapshot> byRepo)
    {
        // First-seen branch order for stable output; group-membership order within each set (the
        // orderedRepoIds are already in that order, so appending as we encounter them preserves it).
        var order = new List<string>();
        var members = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var repoId in orderedRepoIds)
        {
            if (!byRepo.TryGetValue(repoId, out var snap)) continue;
            foreach (var name in snap.LocalBranchNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                // A member's own default branch is never part of a set.
                if (snap.DefaultBranch != null && string.Equals(name, snap.DefaultBranch, StringComparison.Ordinal))
                    continue;
                if (!members.TryGetValue(name, out var list))
                {
                    list = new List<Guid>();
                    members[name] = list;
                    order.Add(name);
                }
                if (!list.Contains(repoId)) list.Add(repoId);
            }
        }

        var result = new List<SyncedBranch>();
        foreach (var name in order)
        {
            var list = members[name];
            if (list.Count >= 2) result.Add(new SyncedBranch(name, list));
        }
        return result;
    }
}
