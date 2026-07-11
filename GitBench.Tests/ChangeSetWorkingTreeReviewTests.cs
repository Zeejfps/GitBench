using GitBench.Features.Commits;
using GitBench.Features.Review;
using Xunit;

namespace GitBench.Tests;

// Phase 5.2 — the cross-repo working-tree review surface. The pure aggregation core is unit-tested here
// (no panel, no git), mirroring the Phase-3 ChangeSetReviewTests patterns:
//  (1) N members' working trees fold into one repo-qualified list with a round-tripping resolver;
//  (2) per-member staged state maps to fully-staged vs. partially-staged qualified marks (the
//      indeterminate checkbox); and
//  (3) a stage/unstage request over qualified paths routes to each owning repo's bare paths, skipping
//      paths already in the requested state.
public sealed class ChangeSetWorkingTreeReviewTests
{
    private static FileChange Mod(string path) => new(path, null, FileChangeStatus.Modified);

    [Fact]
    public void Aggregate_TwoMembers_MergesIntoQualifiedList_GroupedByRepoInMemberOrder()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var members = new[]
        {
            new MemberWorkingTree(a, "svc-a", Unstaged: new[] { Mod("src/index.ts") }, Staged: Array.Empty<FileChange>()),
            new MemberWorkingTree(b, "svc-b", Unstaged: new[] { Mod("src/index.ts") }, Staged: new[] { Mod("README.md") }),
        };

        var agg = ChangeSetWorkingTreeAggregator.Aggregate(members);

        // The same bare path in two repos becomes two distinct qualified identities (the collision case),
        // and members stay in order (svc-a's files precede svc-b's).
        Assert.Equal(
            new[] { "svc-a/src/index.ts", "svc-b/README.md", "svc-b/src/index.ts" },
            agg.Files.Select(f => f.Path));
        // The resolver round-trips each qualified path back to its owning member + bare path.
        Assert.Equal((a, "src/index.ts"), agg.Resolver["svc-a/src/index.ts"]);
        Assert.Equal((b, "src/index.ts"), agg.Resolver["svc-b/src/index.ts"]);
        Assert.Equal((b, "README.md"), agg.Resolver["svc-b/README.md"]);
    }

    [Fact]
    public void Aggregate_StagedState_MapsToFullyStagedAndPartiallyStagedMarks()
    {
        var a = Guid.NewGuid();
        // "clean.cs": staged, no further edits → fully staged.
        // "partial.cs": staged AND edited again (on both sides) → partially staged (indeterminate).
        // "dirty.cs": unstaged only → neither.
        var members = new[]
        {
            new MemberWorkingTree(
                a, "svc-a",
                Unstaged: new[] { Mod("partial.cs"), Mod("dirty.cs") },
                Staged: new[] { Mod("clean.cs"), Mod("partial.cs") }),
        };

        var agg = ChangeSetWorkingTreeAggregator.Aggregate(members);

        Assert.Contains("svc-a/clean.cs", agg.FullyStaged);
        Assert.DoesNotContain("svc-a/partial.cs", agg.FullyStaged);
        Assert.Contains("svc-a/partial.cs", agg.PartlyStaged);
        Assert.DoesNotContain("svc-a/dirty.cs", agg.FullyStaged);
        Assert.DoesNotContain("svc-a/dirty.cs", agg.PartlyStaged);
        // A path present on both sides appears once in the file list (staged entry wins).
        Assert.Single(agg.Files, f => f.Path == "svc-a/partial.cs");
    }

    [Fact]
    public void PlanStage_Staging_GroupsBareTargetsByRepo_SkipsAlreadyFullyStaged()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var resolver = new Dictionary<string, (Guid, string)>
        {
            ["svc-a/one.cs"] = (a, "one.cs"),
            ["svc-a/two.cs"] = (a, "two.cs"),
            ["svc-b/three.cs"] = (b, "three.cs"),
        };
        var fully = new HashSet<string> { "svc-a/two.cs" }; // already staged — must be skipped
        var partly = new HashSet<string>();

        var plan = ChangeSetWorkingTreeAggregator.PlanStage(
            new[] { "svc-a/one.cs", "svc-a/two.cs", "svc-b/three.cs" }, stage: true, fully, partly, resolver);

        // two.cs is already fully staged, so only one.cs (repo a) and three.cs (repo b) are targeted, and
        // each repo gets its own bare-path batch.
        Assert.Equal(new[] { "one.cs" }, plan[a]);
        Assert.Equal(new[] { "three.cs" }, plan[b]);
    }

    [Fact]
    public void PlanStage_Unstaging_TargetsAnythingWithStagedContent_IncludingPartial()
    {
        var a = Guid.NewGuid();
        var resolver = new Dictionary<string, (Guid, string)>
        {
            ["svc-a/full.cs"] = (a, "full.cs"),
            ["svc-a/partial.cs"] = (a, "partial.cs"),
            ["svc-a/clean.cs"] = (a, "clean.cs"),
        };
        var fully = new HashSet<string> { "svc-a/full.cs" };
        var partly = new HashSet<string> { "svc-a/partial.cs" };

        var plan = ChangeSetWorkingTreeAggregator.PlanStage(
            new[] { "svc-a/full.cs", "svc-a/partial.cs", "svc-a/clean.cs" }, stage: false, fully, partly, resolver);

        // clean.cs has nothing staged so it is skipped; the partially staged file can be emptied out.
        Assert.Equal(new[] { "full.cs", "partial.cs" }, plan[a]);
    }

    [Fact]
    public void StagedFileTracker_ToggleViewed_RoutesBarePathToOwningRepo()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var staged = new List<(Guid RepoId, IReadOnlyList<string> Paths)>();
        var unstaged = new List<(Guid RepoId, IReadOnlyList<string> Paths)>();
        var tracker = new ChangeSetStagedFileTracker(
            (repoId, paths) => staged.Add((repoId, paths)),
            (repoId, paths) => unstaged.Add((repoId, paths)));

        var resolver = new Dictionary<string, (Guid RepoId, string Path)>
        {
            ["svc-a/one.cs"] = (a, "one.cs"),
            ["svc-b/two.cs"] = (b, "two.cs"),
        };
        tracker.SetState(resolver, new HashSet<string>(), new HashSet<string>());

        // Checking svc-b/two.cs stages "two.cs" in repo b — the bare path, in the owning repo.
        tracker.ToggleViewed("svc-b/two.cs");

        Assert.Single(staged);
        Assert.Equal(b, staged[0].RepoId);
        Assert.Equal(new[] { "two.cs" }, staged[0].Paths);
        Assert.Empty(unstaged);
    }
}
