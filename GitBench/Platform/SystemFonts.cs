namespace GitBench.Platform;

public readonly record struct SystemFontSpec(string Path, int FaceIndex);

// Locates OS-provided fonts for glyph fallback, so no multi-MB font is bundled.
public static class SystemFonts
{
    /// <summary>
    /// Fonts covering the symbol blocks a terminal program draws its interface out of — Dingbats and
    /// Miscellaneous Technical — which the bundled monospace face does not carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// JetBrains Mono covers Latin, box drawing and the block elements, so a TUI's frame renders and
    /// its checkboxes and spinner do not: a recorded Claude Code session uses ten code points the
    /// face has no glyph for, among them U+2714 (the mark inside a selected checkbox) and the four
    /// asterisks it cycles as a spinner. Without a fallback those cells hold the right code point,
    /// the right colour, and nothing to draw.
    /// </para>
    /// <para>
    /// Two groups because no one system font covers both blocks. The first is asked for the Dingbats
    /// and the ballot boxes and prefers a monospaced face, since a substituted glyph is drawn at the
    /// grid's fixed advance and one designed for that advance sits in the cell rather than beside it.
    /// The second covers the Miscellaneous Technical arrows and circles the first leaves out.
    /// </para>
    /// <para>
    /// Two macOS faces are deliberately absent. Apple Color Emoji carries its glyphs in colour tables
    /// the single-channel glyph atlas cannot read. LastResort covers every code point asked of it and
    /// is the wrong answer for that reason — its glyphs are placeholder boxes, so registering it
    /// would turn every future gap into a box that renders convincingly enough never to be reported.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SystemFontSpec> SymbolFallbacks()
    {
        var result = new List<SystemFontSpec>();
        foreach (var group in SymbolCandidatesByBlock())
            foreach (var spec in group)
                if (File.Exists(spec.Path))
                {
                    result.Add(spec);
                    break;
                }

        return result;
    }

    // Ordered per OS; within a group the first candidate present on disk wins.
    private static IEnumerable<SystemFontSpec[]> SymbolCandidatesByBlock()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Dingbats and ballot boxes: U+2610, U+2612, U+2714, U+2722, U+2733, U+273B, U+273D.
            yield return new SystemFontSpec[]
            {
                new("/System/Library/Fonts/Menlo.ttc", 0),
                new("/System/Library/Fonts/Supplemental/Arial Unicode.ttf", 0),
            };
            // Miscellaneous Technical: U+23BF, U+23F5, U+23FA.
            yield return new SystemFontSpec[]
            {
                new("/System/Library/Fonts/Supplemental/STIXTwoMath.otf", 0),
                new("/System/Library/Fonts/Apple Symbols.ttf", 0),
            };
        }
        else if (OperatingSystem.IsWindows())
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return new SystemFontSpec[]
            {
                new(Path.Combine(fonts, "seguisym.ttf"), 0), // Segoe UI Symbol
                new(Path.Combine(fonts, "arialuni.ttf"), 0),
                new(Path.Combine(fonts, "consola.ttf"), 0),
            };
            yield return new SystemFontSpec[]
            {
                new(Path.Combine(fonts, "cambria.ttc"), 1),  // Cambria Math
                new(Path.Combine(fonts, "seguisym.ttf"), 0),
            };
        }
        else
        {
            yield return new SystemFontSpec[]
            {
                new("/usr/share/fonts/truetype/noto/NotoSansSymbols2-Regular.ttf", 0),
                new("/usr/share/fonts/opentype/noto/NotoSansSymbols2-Regular.ttf", 0),
                new("/usr/share/fonts/google-noto/NotoSansSymbols2-Regular.ttf", 0),
                new("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 0),
            };
            yield return new SystemFontSpec[]
            {
                new("/usr/share/fonts/truetype/noto/NotoSansSymbols-Regular.ttf", 0),
                new("/usr/share/fonts/opentype/noto/NotoSansSymbols-Regular.ttf", 0),
                new("/usr/share/fonts/google-noto/NotoSansSymbols-Regular.ttf", 0),
                new("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 0),
            };
        }
    }

    /// <summary>
    /// One available font per script family (Japanese kana, Simplified Chinese, Korean Hangul). The
    /// shape layer itemizes a line by cmap coverage across every registered fallback, so registering
    /// one font per family lets kana, Han, and Hangul all resolve. For Han ideographs shared across
    /// the families, the earliest-registered font that covers the glyph wins — a regional-variant
    /// caveat we accept until fallback ordering becomes locale-aware.
    /// </summary>
    public static IReadOnlyList<SystemFontSpec> CjkFallbacks()
    {
        var result = new List<SystemFontSpec>();
        foreach (var family in CandidatesByFamily())
            foreach (var spec in family)
                if (File.Exists(spec.Path))
                {
                    result.Add(spec);
                    break;
                }

        return result;
    }

    /// <summary>
    /// The first available Arabic-script font (RTL: Arabic, Persian, Urdu, …), or empty if none is
    /// present. One font suffices — Arabic is a single script — and the cmap-itemizing shape layer
    /// resolves it like any other fallback.
    /// </summary>
    public static IReadOnlyList<SystemFontSpec> ArabicFallbacks()
    {
        foreach (var spec in ArabicCandidates())
            if (File.Exists(spec.Path))
                return new[] { spec };

        return Array.Empty<SystemFontSpec>();
    }

    private static IEnumerable<SystemFontSpec> ArabicCandidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return new("/System/Library/Fonts/Supplemental/GeezaPro.ttc", 0);
            yield return new("/Library/Fonts/GeezaPro.ttc", 0);
            yield return new("/System/Library/Fonts/Supplemental/Arial.ttf", 0);
        }
        else if (OperatingSystem.IsWindows())
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return new(Path.Combine(fonts, "segoeui.ttf"), 0); // Segoe UI (Arabic coverage)
            yield return new(Path.Combine(fonts, "tahoma.ttf"), 0);
            yield return new(Path.Combine(fonts, "arial.ttf"), 0);
        }
        else
        {
            yield return new("/usr/share/fonts/truetype/noto/NotoNaskhArabic-Regular.ttf", 0);
            yield return new("/usr/share/fonts/opentype/noto/NotoNaskhArabic-Regular.ttf", 0);
            yield return new("/usr/share/fonts/google-noto/NotoNaskhArabic-Regular.ttf", 0);
            yield return new("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 0);
        }
    }

    // Ordered per OS; within a family the first candidate present on disk wins.
    private static IEnumerable<SystemFontSpec[]> CandidatesByFamily()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Japanese (kana + JIS kanji).
            yield return new SystemFontSpec[]
            {
                new("/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc", 0),
                new("/System/Library/Fonts/Hiragino Sans GB.ttc", 0),
            };
            // Simplified Chinese.
            yield return new SystemFontSpec[]
            {
                new("/System/Library/Fonts/PingFang.ttc", 0),
                new("/System/Library/Fonts/STHeiti Medium.ttc", 0),
            };
            // Korean Hangul.
            yield return new SystemFontSpec[]
            {
                new("/System/Library/Fonts/AppleSDGothicNeo.ttc", 0),
            };
        }
        else if (OperatingSystem.IsWindows())
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            // Japanese.
            yield return new SystemFontSpec[]
            {
                new(Path.Combine(fonts, "YuGothR.ttc"), 0),   // Yu Gothic
                new(Path.Combine(fonts, "meiryo.ttc"), 0),
                new(Path.Combine(fonts, "msgothic.ttc"), 0),
            };
            // Simplified Chinese.
            yield return new SystemFontSpec[]
            {
                new(Path.Combine(fonts, "msyh.ttc"), 0),      // Microsoft YaHei
                new(Path.Combine(fonts, "simsun.ttc"), 0),
            };
            // Korean Hangul.
            yield return new SystemFontSpec[]
            {
                new(Path.Combine(fonts, "malgun.ttf"), 0),    // Malgun Gothic
            };
        }
        else
        {
            // Noto CJK is a single super-font covering JP/SC/KR, so one registration suffices.
            yield return new SystemFontSpec[]
            {
                new("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", 0),
                new("/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc", 0),
                new("/usr/share/fonts/google-noto-cjk/NotoSansCJK-Regular.ttc", 0),
                new("/usr/share/fonts/opentype/noto/NotoSansCJKjp-Regular.otf", 0),
            };
        }
    }
}
