using System.Text;
using GitBench.Features.Markdown.Rendering;
using Xunit;
using ZGF.AppUtils;

namespace GitBench.Tests.Markdown;

/// <summary>
/// Pins the italic-face contract for markdown emphasis: the family constant the renderer will
/// use, and the embedded <c>Inter-Italic.ttf</c> resource itself — a real TrueType face, not a
/// placeholder or LFS pointer. The resource is loaded exactly the way
/// <c>AppHostSetup.UseAppFonts</c> loads fonts (<see cref="EmbeddedAssets.LoadBytes"/> against
/// the GitBench assembly with the <c>%(Filename)%(Extension)</c> logical name), so a green run
/// proves the registration call site can get the bytes. Whether the family is actually
/// registered with the app host is deliberately not asserted here: grepping AppHostSetup source
/// would be brittle, and registration is runtime-verified by the Step 8 preview via /verify.
/// </summary>
public class MarkdownFontsTests
{
    [Fact]
    public void ItalicFamily_IsInterItalic()
    {
        Assert.Equal("inter-italic", MarkdownFonts.ItalicFamily);
    }

    [Fact]
    public void EmbeddedItalicFace_IsRealTrueTypeFont()
    {
        var bytes = LoadItalicFace();

        // TrueType sfnt version 1.0 magic. Anything else (e.g. a Git LFS pointer file, which is
        // ASCII text) fails here.
        Assert.True(bytes.Length > 4, "Embedded face is too small to even hold the sfnt header.");
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00 }, bytes[..4]);

        // A real Inter face is ~400 KB; a placeholder or pointer file is a few hundred bytes.
        Assert.True(bytes.Length > 100_000,
            $"Embedded face is only {bytes.Length} bytes — a real Inter italic face is far larger.");
    }

    [Fact]
    public void EmbeddedItalicFace_NameTableContainsInterItalic()
    {
        var bytes = LoadItalicFace();

        // Inter's name table stores "Inter Italic" as UTF-16BE (Windows platform records only —
        // verified against the actual Inter release file: no plain-ASCII/Mac-Roman copy exists),
        // so search for the big-endian UTF-16 encoding of the string.
        var needle = Encoding.BigEndianUnicode.GetBytes("Inter Italic");
        Assert.True(ContainsSubsequence(bytes, needle),
            "Embedded face's name table does not contain \"Inter Italic\" (UTF-16BE) — wrong face embedded?");
    }

    private static byte[] LoadItalicFace() =>
        EmbeddedAssets.LoadBytes(typeof(MarkdownFonts).Assembly, "Inter-Italic.ttf");

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
