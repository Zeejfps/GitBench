namespace GitBench.Controls;

/// <summary>
/// The monospaced faces the app draws code and terminal output with.
/// </summary>
/// <remarks>
/// One family name per face rather than one family plus a weight: a canvas resolves a family to
/// exactly one font file and synthesizes bold by emboldening that file's outlines. A synthetic bold
/// advances wider than the face it thickens, which passes unnoticed in prose and is wrong in a cell
/// grid, where the column pitch is fixed and a bold run would bleed. All four faces come from the
/// same JetBrains Mono release, so they share a metric.
/// </remarks>
internal static class MonoFonts
{
    public const string Regular = "jetbrains-mono";
    public const string Bold = "jetbrains-mono-bold";
    public const string Italic = "jetbrains-mono-italic";
    public const string BoldItalic = "jetbrains-mono-bold-italic";
}
