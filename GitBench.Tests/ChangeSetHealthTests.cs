using GitBench.Features.Repos;
using GitBench.Features.Review;
using Xunit;

namespace GitBench.Tests;

// Phase 6 — the set health strip's pure drift computation. ChangeSetMemberHealth.From folds a member's
// loaded stack (ahead-of-base count / whether it resolved) and its RepoStatus probe (unpushed / behind /
// dirty) into one health record; ChangeSetHealth rolls the members up. No window, no git — the strip is
// mostly presentation over this core.
public sealed class ChangeSetHealthTests
{
    // A branch with a tracked upstream, nothing ahead/behind, a clean tree, and a resolved range is fully
    // in sync — no attention, and quiet enough to omit from a per-member drift listing.
    [Fact]
    public void From_TrackedCleanMember_IsInSync()
    {
        var h = ChangeSetMemberHealth.From("svc-a", loadFailed: false, aheadOfBase: 3,
            Status(hasUpstream: true, ahead: 0, behind: 0, dirty: false));

        Assert.False(h.NeedsAttention);
        Assert.True(h.IsQuiet);
        Assert.Equal(3, h.AheadOfBase);   // the reviewed range — not drift
        Assert.Equal(0, h.Unpushed);
        Assert.False(h.NoUpstream);
    }

    // Commits ahead of upstream are unpushed drift; behind is unpulled drift; a dirty tree is drift.
    [Fact]
    public void From_Unpushed_NeedsAttention()
    {
        var h = ChangeSetMemberHealth.From("svc-a", false, 2,
            Status(hasUpstream: true, ahead: 2, behind: 0, dirty: false));

        Assert.True(h.NeedsAttention);
        Assert.Equal(2, h.Unpushed);
        Assert.False(h.IsQuiet);
    }

    [Fact]
    public void From_BehindOrDirty_NeedsAttention()
    {
        var behind = ChangeSetMemberHealth.From("svc-a", false, 0,
            Status(hasUpstream: true, ahead: 0, behind: 4, dirty: false));
        var dirty = ChangeSetMemberHealth.From("svc-b", false, 0,
            Status(hasUpstream: true, ahead: 0, behind: 0, dirty: true));

        Assert.True(behind.NeedsAttention);
        Assert.Equal(4, behind.Behind);
        Assert.True(dirty.NeedsAttention);
        Assert.True(dirty.Dirty);
    }

    // AheadOfBase alone (the change under review) is never drift, no matter how large.
    [Fact]
    public void From_AheadOfBaseOnly_IsNotDrift()
    {
        var h = ChangeSetMemberHealth.From("svc-a", false, 99,
            Status(hasUpstream: true, ahead: 0, behind: 0, dirty: false));

        Assert.False(h.NeedsAttention);
    }

    // No tracking branch: flagged informationally (NoUpstream) but not actionable drift, so a freshly
    // started, not-yet-pushed set still reads as in sync on the aggregate badge.
    [Fact]
    public void From_NoUpstream_IsInformationalNotAttention()
    {
        var h = ChangeSetMemberHealth.From("svc-a", false, 1,
            Status(hasUpstream: false, ahead: 0, behind: 0, dirty: false));

        Assert.True(h.NoUpstream);
        Assert.False(h.NeedsAttention);
        Assert.False(h.IsQuiet);       // still worth a per-member line
        Assert.Equal(0, h.Unpushed);   // ahead/behind are meaningless without an upstream
    }

    // A detached HEAD carries no upstream by definition — don't mislabel it "no upstream".
    [Fact]
    public void From_DetachedHead_IsNotFlaggedNoUpstream()
    {
        var status = new RepoStatus("HEAD", IsDetached: true, HasUpstream: false,
            Ahead: 0, Behind: 0, IsDirty: false, IsBusy: false, HasUnseenError: false);
        var h = ChangeSetMemberHealth.From("svc-a", false, 0, status);

        Assert.False(h.NoUpstream);
        Assert.False(h.NeedsAttention);
    }

    // A member whose range failed to resolve (branch deleted in that repo) is unavailable — the strongest
    // signal — and its status fields are zeroed since they can't be trusted.
    [Fact]
    public void From_LoadFailed_IsUnavailableAndNeedsAttention()
    {
        var h = ChangeSetMemberHealth.From("svc-a", loadFailed: true, aheadOfBase: 5,
            Status(hasUpstream: true, ahead: 9, behind: 9, dirty: true));

        Assert.True(h.Unavailable);
        Assert.True(h.NeedsAttention);
        Assert.Equal(0, h.AheadOfBase);
        Assert.Equal(0, h.Unpushed);
        Assert.False(h.Dirty);
    }

    // The roll-up: AllClear only when no member needs attention; AttentionCount is the exact tally.
    [Fact]
    public void Health_RollsUpAttentionAcrossMembers()
    {
        var clean = ChangeSetMemberHealth.From("svc-a", false, 1, Status(true, 0, 0, false));
        var dirty = ChangeSetMemberHealth.From("svc-b", false, 0, Status(true, 0, 0, true));
        var missing = ChangeSetMemberHealth.From("svc-c", loadFailed: true, 0, Status(true, 0, 0, false));

        var allClear = new ChangeSetHealth(new[] { clean });
        Assert.True(allClear.AllClear);
        Assert.Equal(0, allClear.AttentionCount);

        var mixed = new ChangeSetHealth(new[] { clean, dirty, missing });
        Assert.False(mixed.AllClear);
        Assert.Equal(2, mixed.AttentionCount);
    }

    private static RepoStatus Status(bool hasUpstream, int ahead, int behind, bool dirty) =>
        new("feature/x", IsDetached: false, HasUpstream: hasUpstream,
            Ahead: ahead, Behind: behind, IsDirty: dirty, IsBusy: false, HasUnseenError: false);
}
