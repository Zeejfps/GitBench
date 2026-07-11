namespace GitBench.Features.Review;

/// <summary>
/// The pinned scope of a cross-repo review: a display name (the shared branch name in the convention
/// case) plus one <see cref="ReviewSession"/> per member. Mirrors <see cref="ReviewSession"/>'s
/// pinned-payload role (Locked decision #7): the cross-repo window never tracks the active repo or the
/// live synced-branch index — reopening after membership changes is a new session.
/// </summary>
public sealed record ChangeSetSession(
    string Name,
    IReadOnlyList<ReviewSession> Members);

/// <summary>
/// Broadcast from the branch context menu to open a cross-repo review window over a change set's
/// members. Carries the pinned <see cref="ChangeSetSession"/> (not live state), so the window loads
/// independently and stays locked to that set. Handled by <c>ChangeSetReviewWindowsViewModel</c>.
/// </summary>
public readonly record struct OpenChangeSetReviewMessage(ChangeSetSession Session);
