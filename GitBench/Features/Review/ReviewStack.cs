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
/// A resolved review stack: the linearized first-parent commit list of <c>base..head</c>,
/// oldest→newest. <see cref="Truncated"/> is true when the walk hit its cap before reaching the base.
/// </summary>
public sealed record ReviewStack(
    Guid RepoId,
    string BaseSha,
    string HeadSha,
    string BaseLabel,
    string HeadLabel,
    IReadOnlyList<ReviewIncrement> Increments,
    bool Truncated);
