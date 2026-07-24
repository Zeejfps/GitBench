using GitBench.Features.Branches;
using GitBench.Features.Repos;
using Xunit;

namespace GitBench.Tests;

// BranchTreeBuilder is the single site where the two halves of a branch row's sync state join:
// HEAD's ahead/behind comes from IRepoStatusStore (the same git read that drives the toolbar), every
// other local branch's comes from the for-each-ref listing. These tests pin that ownership — a HEAD
// row must never be able to show a count the status store didn't produce, and a row built without a
// usable status must render no badge at all rather than a confidently wrong one.
public class BranchTreeBuilderTests
{
    private static RepoStatus Status(
        string? branch = "main",
        bool detached = false,
        bool hasUpstream = true,
        int ahead = 0,
        int behind = 0) =>
        new(branch, detached, hasUpstream, ahead, behind, IsDirty: false, IsBusy: false, HasUnseenError: false);

    private static LocalBranchEntry.Head Head(
        string name = "main", HeadUpstreamState upstream = HeadUpstreamState.Tracked) =>
        new(name, "sha-" + name, upstream);

    private static LocalBranchEntry.Other Other(string name, LocalUpstream upstream) =>
        new(name, "sha-" + name, upstream);

    private static LocalBranchEntry.Other Tracked(string name, int ahead = 0, int behind = 0) =>
        Other(name, new LocalUpstream.Tracked("origin", name, new BranchSync(ahead, behind)));

    private static BranchListing Listing(IReadOnlyList<LocalBranchEntry> locals, params RemoteGroup[] remotes) =>
        new(Guid.NewGuid(), locals, remotes, Array.Empty<StashEntry>());

    private static IReadOnlyList<BranchRow> Build(BranchListing listing, RepoStatus status) =>
        BranchTreeBuilder.BuildRows(listing, new BranchesUiState(), status);

    private static LocalBranchRow Local(IReadOnlyList<BranchRow> rows, string name) =>
        rows.OfType<LocalBranchRow>().Single(r => r.Name == name);

    // ---- HEAD's counts come from the status store, and only from there ----

    [Fact]
    public void Head_row_takes_its_sync_from_the_repo_status()
    {
        var rows = Build(Listing([Head()]), Status(ahead: 2, behind: 3));

        Assert.Equal(new BranchSync(2, 3), Local(rows, "main").Sync);
    }

    [Fact]
    public void Non_head_rows_take_their_sync_from_the_listing_not_the_status()
    {
        // The status store only ever describes HEAD; a sibling branch's counts must survive untouched.
        var rows = Build(Listing([Head(), Tracked("feature", ahead: 9, behind: 4)]), Status(ahead: 2, behind: 3));

        Assert.Equal(new BranchSync(9, 4), Local(rows, "feature").Sync);
        Assert.Equal(new BranchSync(2, 3), Local(rows, "main").Sync);
    }

    [Fact]
    public void Head_row_has_no_sync_when_the_status_reports_no_upstream()
    {
        // Counts are meaningless without an upstream — the probe's zeros must not render as "in sync".
        var rows = Build(Listing([Head(upstream: HeadUpstreamState.None)]), Status(hasUpstream: false, ahead: 5, behind: 5));

        Assert.Null(Local(rows, "main").Sync);
    }

    [Fact]
    public void Head_row_has_no_sync_when_the_status_reports_a_detached_head()
    {
        var rows = Build(Listing([Head()]), Status(branch: null, detached: true, ahead: 5, behind: 5));

        Assert.Null(Local(rows, "main").Sync);
    }

    [Fact]
    public void Head_row_has_no_sync_under_an_unknown_status()
    {
        // Before the first probe lands there is nothing to say — render silence, not a zero badge.
        var rows = Build(Listing([Head()]), RepoStatus.Unknown);

        Assert.Null(Local(rows, "main").Sync);
    }

    [Fact]
    public void Head_row_shows_a_zero_sync_when_the_status_says_in_sync()
    {
        // In sync is a real, known answer (badge suppression is the row widget's call, not the join's):
        // it must stay distinguishable from "no upstream", which is null.
        var rows = Build(Listing([Head()]), Status(ahead: 0, behind: 0));

        Assert.Equal(new BranchSync(0, 0), Local(rows, "main").Sync);
    }

    // ---- upstream kind (glyph / name colour) ----

    [Theory]
    [InlineData(HeadUpstreamState.None, BranchUpstreamKind.None)]
    [InlineData(HeadUpstreamState.Gone, BranchUpstreamKind.Gone)]
    [InlineData(HeadUpstreamState.Tracked, BranchUpstreamKind.Tracked)]
    public void Head_row_upstream_kind_mirrors_the_listing_not_the_status(HeadUpstreamState state, BranchUpstreamKind expected)
    {
        // Whether an upstream ref *exists* is a ref-listing fact git status cannot report, so it stays
        // with the entry even though the counts moved to the status store.
        var rows = Build(Listing([Head(upstream: state)]), Status(hasUpstream: true));

        Assert.Equal(expected, Local(rows, "main").Upstream);
    }

    [Fact]
    public void Other_row_upstream_kind_mirrors_its_local_upstream_case()
    {
        var listing = Listing([
            Head(),
            Other("never", new LocalUpstream.None()),
            Other("stale", new LocalUpstream.Gone()),
            Tracked("linked"),
        ]);

        var rows = Build(listing, Status());

        Assert.Equal(BranchUpstreamKind.None, Local(rows, "never").Upstream);
        Assert.Equal(BranchUpstreamKind.Gone, Local(rows, "stale").Upstream);
        Assert.Equal(BranchUpstreamKind.Tracked, Local(rows, "linked").Upstream);
    }

    [Fact]
    public void Other_rows_without_a_tracked_upstream_have_no_sync()
    {
        var listing = Listing([Head(), Other("never", new LocalUpstream.None()), Other("stale", new LocalUpstream.Gone())]);

        var rows = Build(listing, Status(ahead: 7, behind: 7));

        Assert.Null(Local(rows, "never").Sync);
        Assert.Null(Local(rows, "stale").Sync);
    }

    // ---- head-ness and structure ----

    [Fact]
    public void Only_the_head_entry_produces_a_head_row()
    {
        var rows = Build(Listing([Head(), Tracked("feature")]), Status());

        Assert.True(Local(rows, "main").IsHead);
        Assert.False(Local(rows, "feature").IsHead);
    }

    [Fact]
    public void Remote_branches_produce_remote_rows()
    {
        var listing = Listing(
            [Head()],
            new RemoteGroup("origin", [new RemoteBranchEntry("feature/login", "r1")]));

        var rows = Build(listing, Status());

        var remote = Assert.Single(rows.OfType<RemoteBranchRow>());
        Assert.Equal("origin", remote.RemoteName);
        Assert.Equal("feature/login", remote.Name);
        Assert.Equal("login", remote.DisplayName);
    }

    [Fact]
    public void Tree_structure_survives_the_local_entry_split()
    {
        var listing = Listing(
            [Head(), Tracked("feature/login"), Tracked("feature/logout")],
            new RemoteGroup("origin", [new RemoteBranchEntry("main", "r1")]));

        var rows = Build(listing, Status());

        // Folders sort before leaves, so the "feature" folder precedes the "main" branch row.
        Assert.Collection(rows,
            r => Assert.IsType<LocalHeaderRow>(r),
            r => Assert.Equal("feature", Assert.IsType<FolderRow>(r).DisplayName),
            r => Assert.Equal("login", Assert.IsType<LocalBranchRow>(r).DisplayName),
            r => Assert.Equal("logout", Assert.IsType<LocalBranchRow>(r).DisplayName),
            r => Assert.Equal("main", Assert.IsType<LocalBranchRow>(r).DisplayName),
            r => Assert.IsType<RemotesHeaderRow>(r),
            r => Assert.Equal("origin", Assert.IsType<RemoteHeaderRow>(r).RemoteName),
            r => Assert.Equal("main", Assert.IsType<RemoteBranchRow>(r).DisplayName));
    }

    [Fact]
    public void Null_listing_produces_no_rows()
    {
        Assert.Empty(BranchTreeBuilder.BuildRows(null, new BranchesUiState(), Status()));
    }

    [Fact]
    public void A_head_row_and_a_sibling_row_of_the_same_name_are_never_equal()
    {
        // KeyedViewModelList reconciles rows by value; the two cases must not collide when a branch
        // stops (or starts) being HEAD, or the row would keep its stale badge. Both listings put
        // "main" first among two siblings, so the rows match on every field except head-ness.
        var asHead = Local(Build(Listing([Head("main"), Tracked("zeta")]), Status(ahead: 1)), "main");
        var asOther = Local(Build(Listing([Head("zeta"), Tracked("main", ahead: 1)]), Status()), "main");

        Assert.NotEqual(asHead, asOther);
        Assert.Equal(asHead.Sync, asOther.Sync);
    }
}
