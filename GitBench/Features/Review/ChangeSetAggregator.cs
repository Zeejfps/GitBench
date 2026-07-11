namespace GitBench.Features.Review;

/// <summary>
/// One member's contribution to a cross-repo review: either the resolved <see cref="ReviewStack"/>
/// (its <c>base..head</c> range), or an inline load <see cref="Failed"/> carrying the message to show
/// as that member's error group. A member never sinks the whole review — a failed resolution folds into
/// <see cref="Failed"/> beside the others (Locked decision #2 / #5, applied to review loads).
/// </summary>
internal abstract record ChangeSetMemberLoad(Guid RepoId, string RepoKey)
{
    public sealed record Ok(Guid RepoId, string RepoKey, ReviewStack Stack) : ChangeSetMemberLoad(RepoId, RepoKey);
    public sealed record Failed(Guid RepoId, string RepoKey, string Message) : ChangeSetMemberLoad(RepoId, RepoKey);
}

/// <summary>
/// Resolves a change set's members into their review stacks through the same
/// <see cref="IReviewStackSource"/> a single-repo review uses (per-repo base resolution, one call per
/// member — no new git plumbing). The pure aggregation core the cross-repo review view model drives:
/// a thrown load for one member folds into that member's <see cref="ChangeSetMemberLoad.Failed"/> so
/// the rest still aggregate.
/// </summary>
internal static class ChangeSetAggregator
{
    public static ChangeSetMemberLoad LoadMember(
        IReviewStackSource source, ReviewSession member, string repoKey, int cap)
    {
        try
        {
            var stack = source.LoadAsync(member, cap).GetAwaiter().GetResult();
            return new ChangeSetMemberLoad.Ok(member.RepoId, repoKey, stack);
        }
        catch (Exception ex)
        {
            return new ChangeSetMemberLoad.Failed(member.RepoId, repoKey, ex.Message);
        }
    }

    public static IReadOnlyList<ChangeSetMemberLoad> LoadAll(
        IReviewStackSource source,
        IReadOnlyList<ReviewSession> members,
        IReadOnlyDictionary<Guid, string> repoKeys,
        int cap)
    {
        var list = new List<ChangeSetMemberLoad>(members.Count);
        foreach (var m in members)
        {
            var key = repoKeys.TryGetValue(m.RepoId, out var k) ? k : m.RepoId.ToString("N");
            list.Add(LoadMember(source, m, key, cap));
        }
        return list;
    }
}
