namespace GitBench.Platform;

public readonly record struct SystemFontSpec(string Path, int FaceIndex);

// Locates OS-provided CJK fonts for glyph fallback, so no multi-MB font is bundled.
public static class SystemFonts
{
    public static SystemFontSpec? CjkFallback()
    {
        foreach (var spec in CjkCandidates())
            if (File.Exists(spec.Path))
                return spec;
        return null;
    }

    // Ordered per OS; the first candidate present on disk wins.
    private static IEnumerable<SystemFontSpec> CjkCandidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Hiragino Kaku Gothic W3 is the system Japanese sans (regular weight). The GB / PingFang
            // variants and Apple SD Gothic Neo cover Chinese/Korean if Hiragino is ever absent.
            yield return new("/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc", 0);
            yield return new("/System/Library/Fonts/Hiragino Sans GB.ttc", 0);
            yield return new("/System/Library/Fonts/PingFang.ttc", 0);
            yield return new("/System/Library/Fonts/AppleSDGothicNeo.ttc", 0);
        }
        else if (OperatingSystem.IsWindows())
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return new(Path.Combine(fonts, "YuGothR.ttc"), 0);   // Yu Gothic (JP)
            yield return new(Path.Combine(fonts, "YuGothM.ttc"), 0);
            yield return new(Path.Combine(fonts, "meiryo.ttc"), 0);
            yield return new(Path.Combine(fonts, "msgothic.ttc"), 0);
            yield return new(Path.Combine(fonts, "malgun.ttf"), 0);    // Malgun Gothic (KR)
            yield return new(Path.Combine(fonts, "msyh.ttc"), 0);      // Microsoft YaHei (zh)
        }
        else
        {
            yield return new("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", 0);
            yield return new("/usr/share/fonts/opentype/noto/NotoSansCJKjp-Regular.otf", 0);
            yield return new("/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc", 0);
            yield return new("/usr/share/fonts/google-noto-cjk/NotoSansCJK-Regular.ttc", 0);
        }
    }
}
