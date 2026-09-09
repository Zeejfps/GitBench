using GitBench.Controls;
using GitBench.Features.CodeIntel;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// The mark a file wears, wherever it is named: its language's, where the name says which language
/// it is, and a plain page otherwise.
/// </summary>
/// <remarks>
/// Shared by the tree and by the content panel's tabs so a file looks the same in both. The two
/// fonts do not sit the same way within their em — Seti's glyphs are smaller and, being detailed
/// marks rather than line icons, need a little more than parity to read — so the size comes from
/// here too rather than from whichever surface is drawing.
/// </remarks>
internal static class FileGlyph
{
    /// <summary>Lucide's line icons, at the body size the surrounding text is set in.</summary>
    public const float LucideSize = 14f;

    /// <summary>Seti's marks, which need the extra to read at a glance. Tune this one number if
    /// they look off.</summary>
    public const float SetiSize = 17f;

    public static (string Glyph, string Family) For(string name) =>
        // A language this set has no mark for falls back with everything else: Seti covers most of
        // what CodeLanguages detects, but not all of it.
        CodeLanguages.Detect(name) is { } language && SetiIcons.For(language) is { } mark
            ? (mark, SetiIcons.FontFamily)
            : (LucideIcons.File, LucideIcons.FontFamily);

    public static float SizeOf(string family) =>
        family == SetiIcons.FontFamily ? SetiSize : LucideSize;
}
