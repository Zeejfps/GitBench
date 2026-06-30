namespace GitBench.Features.Review;

/// <summary>
/// A pinned review session: the repo and head ref under review plus an optional explicit base.
/// Carried from the branch context menu into the review window so the window stays locked to this
/// range regardless of the main window's active repo. A null <see cref="BaseRef"/> means "auto" —
/// resolved via merge-base once the git backend lands.
/// </summary>
public sealed record ReviewSession(
    Guid RepoId,
    string HeadRef,
    string HeadLabel,
    string? BaseRef,
    string? BaseLabel);

/// <summary>
/// One increment in a review stack: a single commit, reviewed as <c>commit^ → commit</c> so the
/// reviewer sees only that commit's changes. Churn (<see cref="Added"/>/<see cref="Removed"/>) may
/// be filled lazily and defaults to 0.
/// </summary>
public sealed record ReviewIncrement(
    string Sha,
    string ShortSha,
    string Summary,
    string Author,
    DateTimeOffset When,
    int FilesChanged,
    int Added,
    int Removed);

/// <summary>
/// How the review base was chosen, so the header can explain it. <see cref="Upstream"/> /
/// <see cref="DefaultBranch"/> come from auto-resolution; <see cref="Explicit"/> is a base the
/// reviewer picked; <see cref="RawCommit"/> is a base known only by SHA (no ref name).
/// </summary>
public enum ReviewBaseKind
{
    Upstream,
    DefaultBranch,
    Explicit,
    RawCommit,
}

/// <summary>
/// A resolved review base: its commit <see cref="Sha"/>, the human-readable <see cref="Ref"/> it
/// was derived from (e.g. <c>origin/main</c>), and <see cref="Kind"/> (why it was chosen). Carries
/// the ref identity that a bare merge-base SHA throws away, so the header can show a name not a hash.
/// </summary>
public readonly record struct ResolvedReviewBase(string Sha, string Ref, ReviewBaseKind Kind);

/// <summary>
/// A resolved review stack: the linearized first-parent commit list of <c>base..head</c>,
/// oldest→newest. <see cref="Truncated"/> is true when the walk hit its cap before reaching the base.
/// <see cref="BaseRef"/>/<see cref="BaseKind"/> carry the base's ref identity (set by the source) so
/// the header reads <c>origin/main → my-branch</c> rather than a bare SHA; they default to the
/// SHA-only case when only a commit is known.
/// </summary>
public sealed record ReviewStack(
    Guid RepoId,
    string BaseSha,
    string HeadSha,
    string BaseLabel,
    string HeadLabel,
    IReadOnlyList<ReviewIncrement> Increments,
    bool Truncated,
    string? BaseRef = null,
    ReviewBaseKind BaseKind = ReviewBaseKind.RawCommit);
