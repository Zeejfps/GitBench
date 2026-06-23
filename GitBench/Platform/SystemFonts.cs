namespace GitBench.Platform;

public readonly record struct SystemFontSpec(string Path, int FaceIndex);

// Locates OS-provided CJK fonts for glyph fallback, so no multi-MB font is bundled.
public static class SystemFonts
{
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
