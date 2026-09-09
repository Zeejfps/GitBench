using System.Text;

namespace GitBench.Infrastructure;

internal enum LineEnding
{
    Lf,
    CrLf,
    Cr,
}

/// <summary>Which encoding a file's bytes were read in. Anything wider than UTF-8 appears only with
/// a byte order mark, because the mark is the only thing that identifies it here.</summary>
internal enum FileCharset
{
    Utf8,
    Utf8Bom,
    Utf16LeBom,
    Utf16BeBom,
    Utf32LeBom,
    Utf32BeBom,
}

/// <summary>The single interpreter for <see cref="FileCharset"/> and <see cref="LineEnding"/>.</summary>
internal static class FileCharsets
{
    private static readonly Encoding Utf32Be = new UTF32Encoding(bigEndian: true, byteOrderMark: true);

    /// <summary>Every charset a byte order mark can name.</summary>
    public static readonly FileCharset[] Marked =
    [
        FileCharset.Utf8Bom,
        FileCharset.Utf16LeBom,
        FileCharset.Utf16BeBom,
        FileCharset.Utf32LeBom,
        FileCharset.Utf32BeBom,
    ];

    public static Encoding Encoding(this FileCharset charset) => charset switch
    {
        FileCharset.Utf8 or FileCharset.Utf8Bom => System.Text.Encoding.UTF8,
        FileCharset.Utf16LeBom => System.Text.Encoding.Unicode,
        FileCharset.Utf16BeBom => System.Text.Encoding.BigEndianUnicode,
        FileCharset.Utf32LeBom => System.Text.Encoding.UTF32,
        FileCharset.Utf32BeBom => Utf32Be,
        _ => throw new ArgumentOutOfRangeException(nameof(charset), charset, null),
    };

    /// <summary>The bytes that precede the text on disk, which a save writes back before it.</summary>
    public static ReadOnlySpan<byte> Preamble(this FileCharset charset) => charset switch
    {
        FileCharset.Utf8 => default,
        FileCharset.Utf8Bom => [0xEF, 0xBB, 0xBF],
        FileCharset.Utf16LeBom => [0xFF, 0xFE],
        FileCharset.Utf16BeBom => [0xFE, 0xFF],
        FileCharset.Utf32LeBom => [0xFF, 0xFE, 0x00, 0x00],
        FileCharset.Utf32BeBom => [0x00, 0x00, 0xFE, 0xFF],
        _ => throw new ArgumentOutOfRangeException(nameof(charset), charset, null),
    };

    public static string Text(this LineEnding ending) => ending switch
    {
        LineEnding.Lf => "\n",
        LineEnding.CrLf => "\r\n",
        LineEnding.Cr => "\r",
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, null),
    };
}

/// <summary>Everything a write needs to put a file back the way it was found.</summary>
internal sealed record FileEncoding(FileCharset Charset, LineEnding LineEnding, bool EndsWithNewline);

/// <summary>Why the text on screen must not be written back over the file it came from.</summary>
internal enum WriteBackRefusal
{
    Truncated,
    /// <summary>The bytes did not survive the decode, so writing the text back would corrupt the
    /// file.</summary>
    Lossy,
}

/// <summary>Whether the text a preview is showing can be written back over its file.</summary>
internal abstract record FileWriteBack
{
    /// <summary>The bytes decoded and re-encoded to themselves, and the whole file was read.</summary>
    public sealed record Reversible(FileEncoding Encoding) : FileWriteBack;

    public sealed record Refused(WriteBackRefusal Reason) : FileWriteBack;
}
