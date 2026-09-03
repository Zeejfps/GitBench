namespace GitBench.Features.Diff;

/// <summary>
/// How tall each <see cref="DiffRow"/> draws. One function rather than a rule per surface: the
/// single-file pane and the review window both size their virtual lists from row heights and both
/// hit-test against them, and a surface that measured a row differently from the one that drew it
/// would put the caret on the wrong line.
/// </summary>
internal static class DiffRowMetrics
{
    /// <summary>How much shorter a usages row is than a line of code. It annotates the declaration
    /// below it rather than standing beside it as another line of the file, and a full-height one
    /// reads as exactly that.</summary>
    public const float LensHeightRatio = 0.85f;

    public static float HeightOf(DiffRow row, float lineHeight) => row switch
    {
        DiffRow.Banner => lineHeight,
        DiffRow.HunkSeparator => lineHeight,
        DiffRow.Tear => lineHeight,
        DiffRow.Line => lineHeight,
        DiffRow.Lens => lineHeight * LensHeightRatio,
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unhandled diff row kind."),
    };
}
