namespace GitBench.Features.Diff;

internal static class DiffOptions
{
    public const int ContextLines = 3;
    public const int TruncationLineCap = 5000;
    // Lines revealed per click of a hunk-gap expander arrow.
    public const int ContextExpandStep = 20;
    public const int TabWidth = 4;
}
