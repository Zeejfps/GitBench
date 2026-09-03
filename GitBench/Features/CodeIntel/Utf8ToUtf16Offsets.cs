namespace GitBench.Features.CodeIntel;

/// <summary>
/// Turns the UTF-8 byte offsets tree-sitter reports — the only offsets it reports, including the
/// column on a <c>Point</c> — into the UTF-16 code-unit offsets a .NET string is indexed by and the
/// language-server protocol counts positions in. The two are the same number up to the first
/// non-ASCII character in a file, so a missing conversion passes every test written in English and
/// then silently paints a token, or asks a server about a symbol, some bytes to the right in the
/// first file with a CJK string literal above the declaration.
/// </summary>
internal sealed class Utf8ToUtf16Offsets
{
    // An all-ASCII file — which nearly every file is — needs no map and no allocation: there the
    // byte offset already is the UTF-16 offset.
    private static readonly Utf8ToUtf16Offsets Identity = new(null);

    private readonly int[]? _utf16OfByte;

    private Utf8ToUtf16Offsets(int[]? utf16OfByte) => _utf16OfByte = utf16OfByte;

    /// <summary>The mapping for <paramref name="text"/> as encoded into
    /// <paramref name="byteCount"/> UTF-8 bytes.</summary>
    public static Utf8ToUtf16Offsets For(string text, int byteCount) =>
        byteCount == text.Length ? Identity : new Utf8ToUtf16Offsets(Build(text, byteCount));

    /// <summary>The UTF-16 offset of the character whose encoding begins at
    /// <paramref name="byteOffset"/>. A byte in the middle of a character resolves to that
    /// character, and the offset one past the last byte to the end of the text.</summary>
    public int Utf16OffsetOf(uint byteOffset) =>
        _utf16OfByte is null ? (int)byteOffset : _utf16OfByte[byteOffset];

    private static int[] Build(string text, int byteCount)
    {
        var map = new int[byteCount + 1];
        var bi = 0;
        var ci = 0;

        while (ci < text.Length && bi < byteCount)
        {
            var wide = char.IsHighSurrogate(text[ci]) && ci + 1 < text.Length && char.IsLowSurrogate(text[ci + 1]);
            var codepoint = wide ? char.ConvertToUtf32(text[ci], text[ci + 1]) : text[ci];
            var width = codepoint < 0x80 ? 1 : codepoint < 0x800 ? 2 : codepoint < 0x10000 ? 3 : 4;

            for (var k = 0; k < width && bi + k < byteCount; k++) map[bi + k] = ci;

            bi += width;
            ci += wide ? 2 : 1;
        }

        map[byteCount] = text.Length;
        return map;
    }
}
