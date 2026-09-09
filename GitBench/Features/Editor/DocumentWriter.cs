using GitBench.Infrastructure;

namespace GitBench.Features.Editor;

/// <summary>Whether a document reached the disk.</summary>
internal abstract record DocumentSave
{
    private DocumentSave() { }

    public static readonly DocumentSave Ok = new Saved();

    public static DocumentSave Fail(string message) => new Failed(message);

    public sealed record Saved : DocumentSave;

    public sealed record Failed(string Message) : DocumentSave;
}

/// <summary>Puts an edited document back over the file it was read from, in the bytes that file was
/// found in.</summary>
internal static class DocumentWriter
{
    /// <summary>A document's bytes as its file would hold them.</summary>
    public sealed class Bytes
    {
        private readonly byte[] _bytes;

        internal Bytes(byte[] bytes) => _bytes = bytes;

        public ReadOnlySpan<byte> Span => _bytes;
    }

    /// <summary>The file's bytes, from the document. Must run on the thread that owns the document.</summary>
    public static Bytes Serialize(TextDocument document, FileEncoding encoding)
    {
        var text = document.Text;
        var charset = encoding.Charset;
        var preamble = charset.Preamble();
        var bytes = new byte[preamble.Length + charset.Encoding().GetByteCount(text)];

        preamble.CopyTo(bytes);
        charset.Encoding().GetBytes(text, bytes.AsSpan(preamble.Length));
        return new Bytes(bytes);
    }

    /// <summary>Replaces the file with those bytes, through the tmp-then-rename every other writer
    /// in the app uses. Off the UI thread.</summary>
    public static DocumentSave Write(string path, Bytes contents)
    {
        try
        {
            AtomicFile.WriteAllBytes(path, contents.Span);
            return DocumentSave.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return DocumentSave.Fail(ex.Message);
        }
    }
}
