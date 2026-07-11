using GitBench.Features.Review;
using Xunit;

namespace GitBench.Tests;

// Phase 3 — the cross-repo review surface. Three pure cores are unit-tested here (no window, no git):
//  (1) the repo-qualified path scheme round-trips (Locked decision #4);
//  (2) the aggregator folds N members' stacks into one ordered list; and
//  (3) a member whose stack fails to resolve folds into an inline failure without sinking the rest.
// The marks tracker's routing (a mark on a qualified path lands under the owning member's progress key)
// is covered too, since that is the "resolver round-trip" made observable end-to-end.
public sealed class ChangeSetReviewTests
{
    // ---- (1) qualified-path resolver round-trip ----

    [Fact]
    public void RepoQualifiedPaths_QualifyThenResolve_RoundTrips()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var keys = RepoQualifiedPaths.BuildKeys(new[] { (a, "service-a"), (b, "service-b") });
        var byKey = keys.ToDictionary(kv => kv.Value, kv => kv.Key);

        var qualified = RepoQualifiedPaths.Qualify(keys[a], "src/index.ts");

        Assert.Equal("service-a/src/index.ts", qualified);
        Assert.True(RepoQualifiedPaths.TryResolve(qualified, byKey, out var repoId, out var path));
        Assert.Equal(a, repoId);
        Assert.Equal("src/index.ts", path);
    }

    [Fact]
    public void RepoQualifiedPaths_DuplicateDisplayNames_DisambiguateAndStillRoundTrip()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var keys = RepoQualifiedPaths.BuildKeys(new[] { (a, "app"), (b, "app") });
        var byKey = keys.ToDictionary(kv => kv.Value, kv => kv.Key);

        // Two repos sharing a display name still get distinct keys, so the same bare path in each is a
        // distinct identity on the surface (the src/index.ts-in-two-repos collision case).
        Assert.NotEqual(keys[a], keys[b]);
        var qa = RepoQualifiedPaths.Qualify(keys[a], "src/index.ts");
        var qb = RepoQualifiedPaths.Qualify(keys[b], "src/index.ts");
        Assert.NotEqual(qa, qb);

        Assert.True(RepoQualifiedPaths.TryResolve(qa, byKey, out var ra, out var pa));
        Assert.True(RepoQualifiedPaths.TryResolve(qb, byKey, out var rb, out var pb));
        Assert.Equal(a, ra);
        Assert.Equal(b, rb);
        Assert.Equal("src/index.ts", pa);
        Assert.Equal("src/index.ts", pb);
    }

    [Fact]
    public void RepoQualifiedPaths_UnknownLeadingSegment_ReturnsFalseAndEchoesPath()
    {
        var byKey = new Dictionary<string, Guid>();

        Assert.False(RepoQualifiedPaths.TryResolve("bare/path.cs", byKey, out _, out var path));
        Assert.Equal("bare/path.cs", path);
    }

    [Fact]
    public void ChangeSetReviewedFiles_MarkOnQualifiedPath_RoutesToOwningMembersProgressKey()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var store = new ReviewProgressStore();
        var memberByKey = new Dictionary<string, (Guid RepoId, string HeadRef)>
        {
            ["svc-a"] = (a, "feature/x"),
            ["svc-b"] = (b, "feature/x"),
        };
        var tracker = new ChangeSetReviewedFiles(store, memberByKey);
        tracker.SetFingerprints(new Dictionary<string, string?> { ["svc-a/src/index.ts"] = "blob1" });

        tracker.ToggleViewed("svc-a/src/index.ts");

        // The mark landed under member a's (repo, head) key, keyed by the bare path + content id — so a
        // single-repo review of service-a sees it pre-ticked (shared progress).
        Assert.True(store.IsViewed(a, "feature/x", "src/index.ts", "blob1"));
        Assert.True(tracker.IsViewed("svc-a/src/index.ts"));
        // It did not leak to member b, and a different content id reads as unviewed (the file changed).
        Assert.False(store.IsViewed(b, "feature/x", "src/index.ts", "blob1"));
        Assert.False(store.IsViewed(a, "feature/x", "src/index.ts", "blob2"));
    }

    // ---- (2) aggregation of two stub stacks ----

    [Fact]
    public void Aggregator_LoadAll_TwoMembers_ProducesOneOkPerMemberInSessionOrder()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var src = new StubStackSource();
        src.Stacks[a] = Stack(a, "a-base", "a-head");
        src.Stacks[b] = Stack(b, "b-base", "b-head");
        var members = Members(a, b);
        var keys = RepoQualifiedPaths.BuildKeys(new[] { (a, "svc-a"), (b, "svc-b") });

        var loads = ChangeSetAggregator.LoadAll(src, members, keys, cap: 200);

        Assert.Equal(new[] { a, b }, loads.Select(l => l.RepoId));
        var okA = Assert.IsType<ChangeSetMemberLoad.Ok>(loads[0]);
        Assert.Equal("svc-a", okA.RepoKey);
        Assert.Equal("a-base", okA.Stack.BaseSha);
        Assert.Equal("a-head", okA.Stack.HeadSha);
        var okB = Assert.IsType<ChangeSetMemberLoad.Ok>(loads[1]);
        Assert.Equal("svc-b", okB.RepoKey);
        Assert.Equal("b-head", okB.Stack.HeadSha);
    }

    // ---- (3) per-member failure isolation ----

    [Fact]
    public void Aggregator_LoadAll_OneMemberThrows_FoldsIntoFailed_OthersStillOk()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var src = new StubStackSource();
        src.Stacks[a] = Stack(a, "a-base", "a-head");
        src.ThrowFor.Add(b);
        var members = Members(a, b);
        var keys = RepoQualifiedPaths.BuildKeys(new[] { (a, "svc-a"), (b, "svc-b") });

        var loads = ChangeSetAggregator.LoadAll(src, members, keys, cap: 200);

        Assert.IsType<ChangeSetMemberLoad.Ok>(loads[0]);
        var failed = Assert.IsType<ChangeSetMemberLoad.Failed>(loads[1]);
        Assert.Equal("svc-b", failed.RepoKey);
        Assert.Contains(b.ToString("N"), failed.Message);
        // Both members were attempted in order — b throwing never stopped the loop.
        Assert.Equal(new[] { a, b }, src.Calls);
    }

    [Fact]
    public void Aggregator_LoadAll_AllMembersThrow_AllFailed_NotAnException()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var src = new StubStackSource();
        src.ThrowFor.Add(a);
        src.ThrowFor.Add(b);
        var members = Members(a, b);
        var keys = RepoQualifiedPaths.BuildKeys(new[] { (a, "svc-a"), (b, "svc-b") });

        var loads = ChangeSetAggregator.LoadAll(src, members, keys, cap: 200);

        Assert.All(loads, l => Assert.IsType<ChangeSetMemberLoad.Failed>(l));
        Assert.Equal(2, loads.Count);
    }

    private static ReviewSession[] Members(Guid a, Guid b) => new[]
    {
        new ReviewSession(a, "feature/x", "feature/x", null, null),
        new ReviewSession(b, "feature/x", "feature/x", null, null),
    };

    private static ReviewStack Stack(Guid repoId, string baseSha, string headSha) =>
        new(repoId, baseSha, headSha, "origin/main", "feature/x", Array.Empty<ReviewIncrement>(), Truncated: false);

    // A stub stack source: scripted stacks per repo, an opt-in throw set exercising the aggregator's
    // per-member failure fold, and a call log so tests can assert every member was attempted in order.
    private sealed class StubStackSource : IReviewStackSource
    {
        public Dictionary<Guid, ReviewStack> Stacks { get; } = new();
        public HashSet<Guid> ThrowFor { get; } = new();
        public List<Guid> Calls { get; } = new();

        public Task<ReviewStack> LoadAsync(ReviewSession session, int cap)
        {
            Calls.Add(session.RepoId);
            if (ThrowFor.Contains(session.RepoId))
                throw new InvalidOperationException($"no base for {session.RepoId:N}");
            return Task.FromResult(Stacks[session.RepoId]);
        }
    }
}
