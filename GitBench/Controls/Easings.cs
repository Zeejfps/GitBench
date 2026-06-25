namespace GitBench.Controls;

/// <summary>Easing curves mapping linear progress [0,1] to an eased [0,1], for <see cref="Tween"/>.</summary>
internal static class Easings
{
    public static float Linear(float t) => t;

    /// <summary>Fast start, gentle settle — the natural choice for an element easing into place.</summary>
    public static float EaseOutCubic(float t)
    {
        var u = 1f - t;
        return 1f - u * u * u;
    }

    /// <summary>Slow start, fast finish — a placeholder bloom whose alpha stays near zero early, so a
    /// load that resolves within a moment swaps it out before it visibly registers.</summary>
    public static float EaseInCubic(float t) => t * t * t;

    public static float EaseInOutCubic(float t) =>
        t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;
}
