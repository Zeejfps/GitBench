using System.Text;
using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Markdown;

namespace GitBench.Features.FileBrowser;

/// <summary>Why a file has no preview. Each case is a sentence the reader is shown instead.</summary>
internal enum FilePreviewRefusal
{
    /// <summary>NUL bytes near the top: an executable, an archive, an image we cannot decode.</summary>
    Binary,
    /// <summary>Past the hard cap. Reading a 400 MB log into memory to split it into lines is not a
    /// preview.</summary>
    TooLarge,
    /// <summary>Gone between being listed and being asked for.</summary>
    Missing,
    Unreadable,
}

/// <summary>
/// What the preview pane is showing. A sum type rather than a record with a nullable text, a
/// nullable image and an error string, because those are eight states and only these five exist.
/// </summary>
internal abstract record FilePreview
{
    /// <summary>Nothing is selected, or the cursor is on a directory.</summary>
    public sealed record None : FilePreview
    {
        public static readonly None Instance = new();
    }

    public sealed record Loading(string Path) : FilePreview;

    /// <summary>Truncated when the file ran past the text cap: the reader is seeing the first part
    /// of it, and the viewer says so.</summary>
    public sealed record Text(
        string Path,
        IReadOnlyList<string> Lines,
        bool Truncated,
        DiffHighlight? Highlight,
        FileOutline? Outline = null,
        MarkdownRender? Markdown = null) : FilePreview;

    public sealed record Image(string Path, ImagePreview Preview) : FilePreview;

    public sealed record Unavailable(string Path, FilePreviewRefusal Reason) : FilePreview;
}

/// <summary>
/// Turns a file on disk into something the preview pane can draw. Off the UI thread and
/// cancellable — the caller may have moved the cursor three rows on since asking.
/// </summary>
/// <remarks>
/// Format comes from the bytes, not the extension: a <c>.png</c> that is really an LFS pointer
/// fails to decode as a picture and is then read as what it actually is, which is text.
/// </remarks>
internal static class FileContentLoader
{
    /// <summary>Past this, nothing is read at all.</summary>
    public const long MaxPreviewBytes = 64L * 1024 * 1024;

    /// <summary>How much of a file is turned into lines. A file longer than this is shown up to
    /// here and flagged truncated, which is the flag the full-file viewer already carries.</summary>
    public const int MaxTextBytes = 2 * 1024 * 1024;

    private const int SniffBytes = 8 * 1024;

    public static FilePreview Load(
        string absolutePath, ISymbolExtractor extractor, CancellationToken cancellation)
    {
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists) return new FilePreview.Unavailable(absolutePath, FilePreviewRefusal.Missing);
            if (info.Length > MaxPreviewBytes)
                return new FilePreview.Unavailable(absolutePath, FilePreviewRefusal.TooLarge);

            if (TryLoadImage(absolutePath, info.Length, cancellation) is { } image) return image;

            cancellation.ThrowIfCancellationRequested();
            var truncated = info.Length > MaxTextBytes;
            var bytes = ReadCapped(absolutePath, MaxTextBytes, cancellation);

            if (IsBinary(bytes)) return new FilePreview.Unavailable(absolutePath, FilePreviewRefusal.Binary);

            var text = Decode(bytes);
            var lines = SplitLines(text, dropLastPartialLine: truncated);
            return new FilePreview.Text(
                absolutePath,
                lines,
                truncated,
                Highlight(absolutePath, text, truncated),
                Outline(absolutePath, text, extractor),
                Markdown(absolutePath, text, truncated));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FilePreview.Unavailable(absolutePath, FilePreviewRefusal.Unreadable);
        }
    }

    private static FilePreview? TryLoadImage(string path, long length, CancellationToken cancellation)
    {
        if (!ImagePreviewDecoder.IsPreviewablePath(path)) return null;
        if (length > ImagePreviewDecoder.MaxSourceBytes) return null;

        var bytes = ReadCapped(path, ImagePreviewDecoder.MaxSourceBytes, cancellation);
        var decoded = ImagePreviewDecoder.TryDecode(bytes);
        return decoded is null ? null : new FilePreview.Image(path, decoded);
    }

    private static byte[] ReadCapped(string path, int maxBytes, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var captured = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while (captured.Length < maxBytes && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            captured.Write(buffer, 0, (int)Math.Min(read, maxBytes - captured.Length));
        }
        return captured.ToArray();
    }

    private static bool IsBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, SniffBytes);
        for (var i = 0; i < limit; i++)
            if (bytes[i] == 0) return true;
        return false;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    private static IReadOnlyList<string> SplitLines(string text, bool dropLastPartialLine)
    {
        if (text.Length == 0) return [];

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[start..end]);
            start = i + 1;
        }

        if (start < text.Length)
        {
            if (dropLastPartialLine) return lines;
            var end = text.Length;
            if (end > start && text[end - 1] == '\r') end--;
            lines.Add(text[start..end]);
        }

        return lines;
    }

    /// <summary>The declarations in a file, without building a preview of it. For the tree, which
    /// wants a file's shape and none of its lines.</summary>
    public static FileOutline? OutlineOf(
        string absolutePath, ISymbolExtractor extractor, CancellationToken cancellation)
    {
        if (!DiffOptions.StructureEnabled) return null;
        if (CodeLanguages.Detect(absolutePath) is not { } language) return null;

        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists || info.Length > MaxTextBytes) return null;

            var bytes = ReadCapped(absolutePath, MaxTextBytes, cancellation);
            return IsBinary(bytes) ? null : extractor.Extract(Decode(bytes), language);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static FileOutline? Outline(string path, string text, ISymbolExtractor extractor)
    {
        if (!DiffOptions.StructureEnabled) return null;
        if (CodeLanguages.Detect(path) is not { } language) return null;
        return extractor.Extract(text, language);
    }

    private static MarkdownRender? Markdown(string path, string text, bool truncated) =>
        MarkdownFile.IsMarkdownPath(path) ? MarkdownFile.Render(text, truncated) : null;

    private static DiffHighlight? Highlight(string path, string text, bool truncated)
    {
        if (truncated || !DiffOptions.SyntaxHighlightingEnabled) return null;
        if (LanguageRegistry.DetectLanguageId(path) is not { } languageId) return null;
        var spans = RoutedSyntaxHighlighter.Shared.Highlight(text, languageId);
        return spans is null ? null : new DiffHighlight(null, spans);
    }
}
