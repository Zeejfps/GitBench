using System.Text;

using GitBench.Infrastructure;

namespace GitBench.Features.CodeIntel;

/// <summary>A file as the parser takes it: line endings normalized, and the UTF-8 the tree's byte
/// offsets index. Null over the cap, which is what both engines refuse.</summary>
internal readonly record struct ParseText(string Normalized, byte[] Utf8)
{
    /// <summary>Matches <c>FileContentLoader.MaxTextBytes</c>: every file the editor will open is
    /// also parsed.</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    public static ParseText? Of(string text)
    {
        if (text.Length > MaxFileBytes) return null;
        var normalized = TextLines.NormalizeNewlines(text);
        if (Encoding.UTF8.GetByteCount(normalized) > MaxFileBytes) return null;
        return new ParseText(normalized, Encoding.UTF8.GetBytes(normalized));
    }
}
