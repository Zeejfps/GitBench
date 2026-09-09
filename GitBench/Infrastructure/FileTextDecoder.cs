using System.Buffers;
using System.Collections;
using System.Text;

namespace GitBench.Infrastructure;

/// <summary>What a file's bytes came back as: the text, and the verdict on writing it back.</summary>
internal readonly record struct DecodedFile(string Text, FileWriteBack WriteBack);

/// <summary>A file's decoded text, and the display lines it is read as.</summary>
internal sealed class FileText : IReadOnlyList<string>
{
    private readonly List<string> _lines;

    public FileText(string text, bool dropLastPartialLine = false)
    {
        Text = text;
        _lines = TextLines.Split(text, dropLastPartialLine);
    }

    /// <summary>The file exactly as it decoded, terminators and all. This is what a save writes
    /// back.</summary>
    public string Text { get; }

    public int Count => _lines.Count;

    public string this[int index] => _lines[index];

    public List<string>.Enumerator GetEnumerator() => _lines.GetEnumerator();

    IEnumerator<string> IEnumerable<string>.GetEnumerator() => _lines.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _lines.GetEnumerator();
}

/// <summary>Turns a file's bytes into text and, in the same pass, into the answer to "may this be
/// written back".</summary>
internal static class FileTextDecoder
{
    public static DecodedFile Decode(ReadOnlySpan<byte> bytes, bool truncated)
    {
        var charset = CharsetOf(bytes);
        var payload = bytes[charset.Preamble().Length..];
        var text = charset.Encoding().GetString(payload);
        return new DecodedFile(text, WriteBackOf(payload, text, charset, truncated));
    }

    /// <summary>The text alone, for the readers that only ever render it.</summary>
    public static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        var charset = CharsetOf(bytes);
        return charset.Encoding().GetString(bytes[charset.Preamble().Length..]);
    }

    private static FileWriteBack WriteBackOf(
        ReadOnlySpan<byte> payload, string text, FileCharset charset, bool truncated)
    {
        if (truncated) return new FileWriteBack.Refused(WriteBackRefusal.Truncated);
        if (!Reproduces(payload, text, charset.Encoding()))
            return new FileWriteBack.Refused(WriteBackRefusal.Lossy);

        return new FileWriteBack.Reversible(
            new FileEncoding(charset, DominantLineEnding(text), EndsWithNewline(text)));
    }

    /// <summary>The charset a file's leading bytes declare. The longest mark that matches wins,
    /// because UTF-16 LE's is a prefix of UTF-32 LE's.</summary>
    public static FileCharset CharsetOf(ReadOnlySpan<byte> bytes)
    {
        var found = FileCharset.Utf8;
        var length = 0;
        foreach (var charset in FileCharsets.Marked)
        {
            var mark = charset.Preamble();
            if (mark.Length <= length || !bytes.StartsWith(mark)) continue;

            found = charset;
            length = mark.Length;
        }

        return found;
    }

    private static bool Reproduces(ReadOnlySpan<byte> payload, string text, Encoding encoding)
    {
        if (encoding.GetByteCount(text) != payload.Length) return false;

        var buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        try
        {
            var written = encoding.GetBytes(text, buffer);
            return payload.SequenceEqual(buffer.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The ending a line added later should get; a tie and a file with no breaks fall to
    /// LF.</summary>
    private static LineEnding DominantLineEnding(string text)
    {
        var (lf, crlf, cr) = (0, 0, 0);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lf++;
            }
            else if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
        }

        if (crlf > lf && crlf >= cr) return LineEnding.CrLf;
        return cr > lf && cr > crlf ? LineEnding.Cr : LineEnding.Lf;
    }

    private static bool EndsWithNewline(string text) =>
        text.Length > 0 && text[^1] is '\n' or '\r';
}
