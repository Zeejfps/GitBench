namespace GitBench.Controls;

/// <summary>
/// Computes the breathing fill for loading-skeleton blocks from a <see cref="Pulse"/> phase: a faint
/// overlay of a theme-neutral base color, so the skeleton reads on any surface and adapts to light/dark
/// on its own.
/// </summary>
internal static class SkeletonPainter
{
    // Alphas the blocks breathe between (over the base color). Low — a skeleton is a hint of shape,
    // not solid content.
    private const float MinAlpha = 0x12 / 255f;
    private const float MaxAlpha = 0x2A / 255f;

    /// <summary>The block fill for a <paramref name="baseColor"/> (theme neutral) at a pulse phase
    /// [0,1]. <paramref name="dim"/> sinks a secondary block (e.g. a section header) beneath the
    /// primary rows.</summary>
    public static uint Fill(uint baseColor, float pulse, float dim = 1f)
    {
        var alpha = (MinAlpha + (MaxAlpha - MinAlpha) * pulse) * dim;
        var a = (uint)Math.Clamp(alpha * 255f, 0f, 255f);
        return (baseColor & 0x00FFFFFFu) | (a << 24);
    }
}
