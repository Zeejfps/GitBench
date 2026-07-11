using GitBench.Features.Repos;

namespace GitBench.Features.Review;

/// <summary>
/// One member's drift/health signals for the cross-repo review header strip (Phase 6.1). Composed from
/// the member's loaded stack (its <c>base..head</c> range) and its <see cref="RepoStatus"/> probe
/// (ahead/behind its upstream, dirty working tree). A member whose range failed to resolve — the branch
/// was deleted in that repo, or the range could not be computed — is <see cref="Unavailable"/>.
///
/// <para><see cref="NeedsAttention"/> is the actionable-drift signal that flips the strip's aggregate
/// badge: unpushed or behind commits, a dirty tree, or an unavailable member. A non-zero
/// <see cref="AheadOfBase"/> is the normal case (it <em>is</em> the change under review) and never counts
/// as drift; a missing upstream (<see cref="NoUpstream"/>) is shown informationally but is not treated as
/// drift, so a freshly-started (not-yet-pushed) set still reads as in sync.</para>
/// </summary>
public readonly record struct ChangeSetMemberHealth(
    string RepoKey,
    bool Unavailable,
    int AheadOfBase,
    int Unpushed,
    int Behind,
    bool NoUpstream,
    bool Dirty)
{
    public bool NeedsAttention => Unavailable || Unpushed > 0 || Behind > 0 || Dirty;

    /// <summary>Whether nothing at all is noteworthy — used to decide if a per-member line is worth
    /// listing (a clean, fully-pushed member with a tracked upstream adds no information).</summary>
    public bool IsQuiet => !NeedsAttention && !NoUpstream;

    /// <summary>Builds a member's health from the two sources the strip reads: whether its stack
    /// resolved, its ahead-of-base commit count (the reviewed range), and its live status probe.</summary>
    public static ChangeSetMemberHealth From(string repoKey, bool loadFailed, int aheadOfBase, RepoStatus status)
    {
        if (loadFailed)
            return new ChangeSetMemberHealth(repoKey, Unavailable: true, 0, 0, 0, NoUpstream: false, Dirty: false);

        return new ChangeSetMemberHealth(
            repoKey,
            Unavailable: false,
            AheadOfBase: aheadOfBase,
            Unpushed: status.HasUpstream ? status.Ahead : 0,
            Behind: status.HasUpstream ? status.Behind : 0,
            NoUpstream: !status.HasUpstream && !status.IsDetached,
            Dirty: status.IsDirty);
    }
}

/// <summary>
/// The whole set's health for the review header (Phase 6.1): one <see cref="ChangeSetMemberHealth"/> per
/// member, in the session's member order, plus the two roll-ups the header binds — whether every member
/// is clear and how many need attention. Pure and presentation-agnostic; the header formats it, and unit
/// tests drive it directly.
/// </summary>
public sealed record ChangeSetHealth(IReadOnlyList<ChangeSetMemberHealth> Members)
{
    public static readonly ChangeSetHealth Empty = new(Array.Empty<ChangeSetMemberHealth>());

    /// <summary>No member has actionable drift (an unavailable member counts as not-clear).</summary>
    public bool AllClear => Members.All(m => !m.NeedsAttention);

    /// <summary>How many members have actionable drift — the count the aggregate badge shows.</summary>
    public int AttentionCount => Members.Count(m => m.NeedsAttention);
}
