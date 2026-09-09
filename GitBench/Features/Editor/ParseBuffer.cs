using System.Text;

using TreeSitter.Bindings;

namespace GitBench.Features.Editor;

/// <summary>
/// One document as a parser reads it: its UTF-8 bytes with the line endings normalized, and the
/// index of where each line starts in them. Follows the document one edit at a time and describes
/// each edit in the byte offsets an incremental re-parse takes.
/// </summary>
/// <remarks>
/// <para>
/// Everything the mapping needs is here. Nothing is read back out of the document, which is what
/// makes the trap structural rather than remembered: the edit's range is in <em>post</em>-edit
/// coordinates while the offsets it has to become are in <em>pre</em>-edit ones, so a mapping that
/// reaches for a line of the document reads the line the edit just produced.
/// </para>
/// <para>
/// Document lines and buffer lines are one to one. <see cref="TextDocument"/> counts CRLF and a lone
/// CR as one terminator each, and normalizing maps each to one <c>\n</c>; and because a lone CR
/// always ends a line, no line's own characters contain one, so a column counted in UTF-16 code
/// units of a document line counts the same characters of a buffer line.
/// </para>
/// <para>
/// Only the byte offsets decide the tree an incremental parse produces — the parser is handed the
/// whole new buffer and re-derives every row and column from it — so the points are bookkeeping the
/// header asks for, not something a test of the tree can ever pin. The line index is the part worth
/// doubting: it is spliced, not shifted, because an edit changes the number of lines.
/// </para>
/// </remarks>
internal sealed class ParseBuffer
{
    // 1-based lines: _lineStart[n - 1] is the byte offset line n begins at. Always at least one
    // entry, matching TextDocument.LineCount counting the empty line a trailing terminator opens.
    private readonly List<int> _lineStart = [0];

    private byte[] _utf8;

    public ParseBuffer(string text)
    {
        _utf8 = Encoding.UTF8.GetBytes(NormalizeNewlines(text ?? string.Empty));
        for (var i = 0; i < _utf8.Length; i++)
        {
            if (_utf8[i] == (byte)'\n') _lineStart.Add(i + 1);
        }
    }

    /// <summary>The bytes as they stand. A fresh array after every edit, because a parse takes a
    /// buffer it can hold on to.</summary>
    public byte[] Utf8 => _utf8;

    /// <summary>The number of lines, counting the empty one a trailing terminator opens.</summary>
    public int LineCount => _lineStart.Count;

    /// <summary>The bytes as the string the queries index their captures back into.</summary>
    public string Text() => Encoding.UTF8.GetString(_utf8);

    /// <summary>
    /// Follows one edit into the buffer and returns it as a parser takes it.
    /// </summary>
    /// <param name="inverse">The edit that would undo the change: its range names where the new text
    /// now sits, and its replacement is the text that was there before.</param>
    /// <param name="inserted">The text the change put in — the document's own characters between the
    /// two ends of <paramref name="inverse"/>'s range. Not recoverable from the inverse.</param>
    public TSInputEdit Follow(TextEdit inverse, string inserted)
    {
        // The start is the one position that means the same thing in both coordinate systems; the
        // old end is it advanced by the text the edit removed, which is the inverse's replacement.
        var start = inverse.Range.Start;
        var oldEnd = TextEdit.EndOfReplacement(inverse);

        var startByte = ByteOffsetOf(start);
        var oldEndByte = ByteOffsetOf(oldEnd);
        var startPoint = PointAt(start.Line.Value, startByte);
        var oldEndPoint = PointAt(oldEnd.Line.Value, oldEndByte);

        var insertedBytes = Encoding.UTF8.GetBytes(NormalizeNewlines(inserted));
        var newEndByte = startByte + insertedBytes.Length;

        var openedLines = Splice(startByte, oldEndByte, insertedBytes, start.Line.Value, oldEnd.Line.Value);

        return new TSInputEdit
        {
            StartByte = (uint)startByte,
            OldEndByte = (uint)oldEndByte,
            NewEndByte = (uint)newEndByte,
            StartPoint = startPoint,
            OldEndPoint = oldEndPoint,
            NewEndPoint = PointAt(start.Line.Value + openedLines, newEndByte),
        };
    }

    /// <summary>Where a document position sits in the bytes. The column is in UTF-16 code units and
    /// has to be walked into bytes: the two are the same number only until the first non-ASCII
    /// character on the line.</summary>
    private int ByteOffsetOf(TextPosition position)
    {
        var line = position.Line.Value;
        if (line < 1 || line > _lineStart.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position), position, $"The buffer has {_lineStart.Count} lines.");
        }

        var at = _lineStart[line - 1];
        // The line's own bytes, its terminator excluded.
        var end = line < _lineStart.Count ? _lineStart[line] - 1 : _utf8.Length;

        var remaining = position.Column.Value;
        while (remaining > 0 && at < end)
        {
            var width = Utf8SequenceLength(_utf8[at]);
            // Everything outside the basic plane is two UTF-16 code units and four UTF-8 bytes.
            remaining -= width == 4 ? 2 : 1;
            at += width;
        }

        return at;
    }

    // Tree-sitter counts a point's column in bytes, so it is the distance from the line's start.
    private TSPoint PointAt(int line, int byteOffset) => new()
    {
        Row = (uint)(line - 1),
        Column = (uint)(byteOffset - _lineStart[line - 1]),
    };

    /// <summary>Replaces the bytes the edit covered and re-indexes the lines around them. Returns how
    /// many lines the inserted text opens.</summary>
    private int Splice(int startByte, int oldEndByte, byte[] inserted, int startLine, int oldEndLine)
    {
        var next = new byte[_utf8.Length - (oldEndByte - startByte) + inserted.Length];
        _utf8.AsSpan(0, startByte).CopyTo(next);
        inserted.CopyTo(next.AsSpan(startByte));
        _utf8.AsSpan(oldEndByte).CopyTo(next.AsSpan(startByte + inserted.Length));
        _utf8 = next;

        // The line the edit starts on does not move, so the change begins one entry later. The
        // entries between there and the old end stand for lines the edit swallowed and go; the
        // inserted text opens however many its own breaks do; the tail then shifts by the change in
        // length. Shifting from the start line onward instead gets a whole-line deletion wrong.
        _lineStart.RemoveRange(startLine, oldEndLine - startLine);

        var opened = 0;
        for (var i = 0; i < inserted.Length; i++)
        {
            if (inserted[i] != (byte)'\n') continue;
            _lineStart.Insert(startLine + opened, startByte + i + 1);
            opened++;
        }

        var shift = inserted.Length - (oldEndByte - startByte);
        for (var i = startLine + opened; i < _lineStart.Count; i++) _lineStart[i] += shift;

        return opened;
    }

    private static int Utf8SequenceLength(byte lead) =>
        lead < 0x80 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;

    /// <summary>
    /// The same normalization both parse paths do, applied to a fragment rather than a file.
    /// </summary>
    /// <remarks>
    /// Safe on a fragment only because <c>TextDocument.KeepTerminatorsWhole</c> makes the one
    /// dangerous case unrepresentable: a replacement ending in <c>\r</c> immediately before an
    /// existing <c>\n</c> would normalize to two breaks where the document counts one, and the edit
    /// is widened to swallow the pair rather than split it.
    /// </remarks>
    private static string NormalizeNewlines(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
}
