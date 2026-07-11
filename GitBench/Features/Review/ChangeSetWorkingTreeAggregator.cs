using GitBench.Features.Commits;

namespace GitBench.Features.Review;

/// <summary>
/// One member's working tree for the cross-repo working-tree surface: the repo, its de-duplicated
/// <see cref="RepoKey"/>, and its unstaged / staged file lists (bare repo-relative paths, exactly as
/// <c>GetLocalChanges</c> returns them).
/// </summary>
internal sealed record MemberWorkingTree(
    Guid RepoId,
    string RepoKey,
    IReadOnlyList<FileChange> Unstaged,
    IReadOnlyList<FileChange> Staged);

/// <summary>
/// The aggregation of N members' working trees into one repo-qualified surface: the merged file list
/// (grouped by repo, member order preserved), the <c>qualified → (member, bare path)</c> resolver, and
/// the per-qualified-path staged state (fully staged vs. partially staged — the indeterminate mark).
/// </summary>
internal sealed record AggregatedWorkingTree(
    IReadOnlyList<FileChange> Files,
    IReadOnlyDictionary<string, (Guid RepoId, string Path)> Resolver,
    IReadOnlySet<string> FullyStaged,
    IReadOnlySet<string> PartlyStaged);

/// <summary>
/// The pure aggregation core of the cross-repo working-tree review (Phase 5.2) — the working-tree twin
/// of <see cref="ChangeSetAggregator"/>. Merges each member's unstaged/staged lists into one qualified
/// file list (Locked decision #4), keeping a resolver so git-facing stage/unstage calls always receive
/// the bare path plus the owning member. Staged state mirrors <c>StagedFileTracker</c>'s rule, per
/// member: a path is <em>fully</em> staged only when it is in Staged and absent from Unstaged; a path in
/// both is <em>partially</em> staged. No git, no threading — the aggregation and the stage routing are
/// unit-testable directly.
/// </summary>
internal static class ChangeSetWorkingTreeAggregator
{
    public static AggregatedWorkingTree Aggregate(IReadOnlyList<MemberWorkingTree> members)
    {
        var resolver = new Dictionary<string, (Guid, string)>(StringComparer.Ordinal);
        var files = new List<FileChange>();
        var fully = new HashSet<string>(StringComparer.Ordinal);
        var partly = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in members)
        {
            var unstagedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in member.Unstaged) unstagedPaths.Add(f.Path);

            var memberFiles = MergeStagedWins(member.Unstaged, member.Staged);

            foreach (var f in memberFiles)
            {
                var qualified = RepoQualifiedPaths.Qualify(member.RepoKey, f.Path);
                files.Add(f with
                {
                    Path = qualified,
                    OldPath = f.OldPath == null ? null : RepoQualifiedPaths.Qualify(member.RepoKey, f.OldPath),
                });
                resolver[qualified] = (member.RepoId, f.Path);
            }

            foreach (var f in member.Staged)
            {
                var qualified = RepoQualifiedPaths.Qualify(member.RepoKey, f.Path);
                if (unstagedPaths.Contains(f.Path)) partly.Add(qualified);
                else fully.Add(qualified);
            }
        }

        return new AggregatedWorkingTree(files, resolver, fully, partly);
    }

    /// <summary>
    /// One member's unstaged + staged lists merged into a single per-file list, sorted by path. A path
    /// present on both sides (staged, then edited again) takes its staged entry — that status is the one
    /// measured against HEAD, which is what the file's diff shows. Shared by <see cref="Aggregate"/> (for
    /// the marks) and the host (for the details sections) so both order and de-duplicate identically.
    /// </summary>
    public static IReadOnlyList<FileChange> MergeStagedWins(
        IReadOnlyList<FileChange> unstaged, IReadOnlyList<FileChange> staged)
    {
        var merged = new Dictionary<string, FileChange>(StringComparer.Ordinal);
        foreach (var f in unstaged) merged[f.Path] = f;
        foreach (var f in staged) merged[f.Path] = f;
        var list = new List<FileChange>(merged.Values);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    /// <summary>
    /// Groups a stage/unstage request over qualified paths into per-member bare-path batches, filtering
    /// out paths that are already in the requested state — staging targets anything not <em>fully</em>
    /// staged (a partially staged file has more to capture), unstaging targets anything with <em>any</em>
    /// staged content (so a partially staged file can be emptied back out), the same rule
    /// <c>StagedFileTracker.SetViewed</c> applies per repo. Pure, so the stage routing is unit-testable.
    /// </summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<string>> PlanStage(
        IReadOnlyList<string> qualifiedPaths,
        bool stage,
        IReadOnlySet<string> fullyStaged,
        IReadOnlySet<string> partlyStaged,
        IReadOnlyDictionary<string, (Guid RepoId, string Path)> resolver)
    {
        var byRepo = new Dictionary<Guid, List<string>>();
        foreach (var qualified in qualifiedPaths)
        {
            var hasStaged = fullyStaged.Contains(qualified) || partlyStaged.Contains(qualified);
            var wanted = stage ? !fullyStaged.Contains(qualified) : hasStaged;
            if (!wanted) continue;
            if (!resolver.TryGetValue(qualified, out var target)) continue;
            if (!byRepo.TryGetValue(target.RepoId, out var list))
                byRepo[target.RepoId] = list = new List<string>();
            list.Add(target.Path);
        }

        var result = new Dictionary<Guid, IReadOnlyList<string>>(byRepo.Count);
        foreach (var (repoId, list) in byRepo) result[repoId] = list;
        return result;
    }
}
