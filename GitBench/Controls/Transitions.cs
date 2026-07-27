namespace GitBench.Controls;

/// <summary>
/// Shared timing for the view-transition animations that soften a repo's loading→loaded swap, so
/// every panel (commits, branches, local changes) enters and blooms in step.
/// </summary>
internal static class Transitions
{
    /// <summary>A repo's content fading into place once its data arrives.</summary>
    public const float ContentEnterSeconds = 0.25f;

    /// <summary>A loading placeholder blooming in. Longer and ease-in (see <see cref="Easings.EaseInCubic"/>)
    /// so a load that resolves within a moment replaces a placeholder that never visibly appeared.</summary>
    public const float PlaceholderBloomSeconds = 0.3f;

    /// <summary>How long a "just went in sync" mark stands in for a branch's ahead/behind badge. Long
    /// enough to cover the refresh that follows the operation, so the counts it superseded are never
    /// briefly shown again. The mark's own fade and the state that mounts it share this number.</summary>
    public const float SyncMarkSeconds = 1.0f;
}
