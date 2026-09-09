using System.Text;
using GitBench.Features.Diff;

namespace GitBench.Features.Editor;

/// <summary>The text being edited, held as a piece table over the loaded text and an append-only
/// buffer. Every change enters through <see cref="Apply"/> as one edit and leaves as its inverse.</summary>
internal sealed class TextDocument
{
    private enum PieceBuffer
    {
        Original,
        Add,
    }

    /// <summary>A run of one buffer, with the number of line breaks that end inside it.</summary>
    private readonly record struct Piece(PieceBuffer Buffer, int Start, int Length, int Breaks);

    private sealed class Node
    {
        public Node(Piece piece, int priority)
        {
            Piece = piece;
            Priority = priority;
            Length = piece.Length;
            Breaks = piece.Breaks;
        }

        public Piece Piece;
        public Node? Left;
        public Node? Right;
        public readonly int Priority;
        public int Length;
        public int Breaks;
    }

    private const int PrioritySeed = 0x5EED;

    private readonly string _original;
    private readonly int[] _originalBreaks;
    private readonly StringBuilder _add = new();
    private readonly List<int> _addBreaks = new();
    private readonly Random _priorities = new(PrioritySeed);
    private Node? _root;

    private TextDocument(string original)
    {
        _original = original;
        _originalBreaks = BreakOffsets(original, 0);
        if (original.Length > 0)
            _root = NewNode(new Piece(PieceBuffer.Original, 0, original.Length, _originalBreaks.Length));
    }

    /// <summary>Reads a decoded file into a document, taking the text exactly as given.</summary>
    public static TextDocument FromText(string text) => new(text ?? string.Empty);

    /// <summary>Bumped by every edit that changes the text.</summary>
    public int Revision { get; private set; }

    /// <summary>Raised once per edit that changed the text.</summary>
    public event Action? Changed;

    public int Length => Len(_root);

    /// <summary>The number of lines, counting the empty one a trailing terminator opens:
    /// <c>"a\n"</c> is two lines and <c>"a"</c> is one.</summary>
    public int LineCount => Breaks(_root) + 1;

    /// <summary>Whether the text ends with a line terminator.</summary>
    public bool EndsWithNewline => Breaks(_root) > 0 && LineStartOffset(LineCount - 1) == Length;

    /// <summary>The position past the last character, and the far end of a select-all.</summary>
    public TextPosition End => new(new FileLine(LineCount), new RawColumn(LineContentLength(LineCount - 1)));

    /// <summary>How many pieces the document is currently spelled out of, for the tests that hold
    /// the cost model honest.</summary>
    public int PieceCount => CountNodes(_root);

    /// <summary>How many characters have been appended since load, for the tests that hold the cost
    /// model honest.</summary>
    public int AppendedCharCount => _add.Length;

    /// <summary>The line's own characters, without the terminator that ended it.</summary>
    public string Line(FileLine line)
    {
        var index = Clamp(new TextPosition(line, new RawColumn(0))).Line.Value - 1;
        var start = LineStartOffset(index);
        return SliceOffsets(start, start + LineContentLength(index));
    }

    public string Slice(TextRange range)
    {
        var clamped = Clamp(range);
        return SliceOffsets(OffsetOf(clamped.Start), OffsetOf(clamped.End));
    }

    /// <summary>The whole document as one string. Materializes it, so this is for saving and for
    /// tests — not for drawing, which reads <see cref="Line"/> per visible row.</summary>
    public string Text => SliceOffsets(0, Length);

    /// <summary>The one place a position out of the document's range is brought back into it.</summary>
    public TextPosition Clamp(TextPosition position)
    {
        var line = Math.Clamp(position.Line.Value, 1, LineCount);
        var column = Math.Clamp(position.Column.Value, 0, LineContentLength(line - 1));
        return TextPosition.At(line, column);
    }

    /// <summary>A range brought inside the document. One that lies entirely past the end has no
    /// characters left to cover and comes back as a caret.</summary>
    public TextRange Clamp(TextRange range)
    {
        var start = Clamp(range.Start);
        var end = Clamp(range.End);
        return end < start ? TextRange.Caret(start) : new TextRange(start, end);
    }

    /// <summary>Replaces the edit's range with its replacement and returns the edit that puts the
    /// document back. An edit that removes and inserts nothing is dropped: no revision, an empty
    /// inverse.</summary>
    public TextEdit Apply(TextEdit edit) => Apply(edit, out _);

    /// <summary>Applies an edit and reports through <paramref name="applied"/> the forward edit as
    /// the document took it, which may be wider than the one given. A position carried across the
    /// change must be shifted by that one, not by the caller's.</summary>
    public TextEdit Apply(TextEdit edit, out TextEdit applied)
    {
        var range = Clamp(edit.Range);
        var start = OffsetOf(range.Start);
        var end = OffsetOf(range.End);
        var replacement = edit.Replacement;
        (start, end, replacement) = KeepTerminatorsWhole(start, end, replacement);

        var removed = SliceOffsets(start, end);
        applied = new TextEdit(new TextRange(PositionAt(start), PositionAt(end)), replacement);
        if (removed.Length == 0 && replacement.Length == 0)
        {
            applied = new TextEdit(TextRange.Caret(applied.Range.Start), string.Empty);
            return applied;
        }

        Split(_root, end, out var head, out var tail);
        Split(head, start, out var left, out _);

        Node? inserted = null;
        if (replacement.Length > 0)
        {
            var addStart = _add.Length;
            var breaks = BreakOffsets(replacement, addStart);
            _add.Append(replacement);
            _addBreaks.AddRange(breaks);
            inserted = NewNode(new Piece(PieceBuffer.Add, addStart, replacement.Length, breaks.Length));
        }

        _root = Merge(Merge(left, inserted), tail);
        Revision++;
        Changed?.Invoke();

        return new TextEdit(new TextRange(applied.Range.Start, PositionAt(start + replacement.Length)), removed);
    }

    /// <summary>Widens an edit that would otherwise leave a CRLF split across two pieces, which
    /// would count as two line breaks. The text the edit produces is unchanged.</summary>
    private (int Start, int End, string Replacement) KeepTerminatorsWhole(int start, int end, string replacement)
    {
        var before = start > 0 ? CharAt(start - 1) : '\0';
        var after = end < Length ? CharAt(end) : '\0';
        var opensWithLf = replacement.Length > 0 ? replacement[0] == '\n' : after == '\n';
        var closesWithCr = replacement.Length > 0 ? replacement[^1] == '\r' : before == '\r';

        if (before == '\r' && opensWithLf)
        {
            start--;
            replacement = '\r' + replacement;
        }

        if (after == '\n' && closesWithCr)
        {
            end++;
            replacement += '\n';
        }

        return (start, end, replacement);
    }

    private Node NewNode(Piece piece) => new(piece, _priorities.Next());

    private static int Len(Node? node) => node?.Length ?? 0;

    private static int Breaks(Node? node) => node?.Breaks ?? 0;

    private static void Update(Node node)
    {
        node.Length = Len(node.Left) + node.Piece.Length + Len(node.Right);
        node.Breaks = Breaks(node.Left) + node.Piece.Breaks + Breaks(node.Right);
    }

    private static int CountNodes(Node? node) =>
        node == null ? 0 : 1 + CountNodes(node.Left) + CountNodes(node.Right);

    private static Node? Merge(Node? left, Node? right)
    {
        if (left == null) return right;
        if (right == null) return left;

        if (left.Priority > right.Priority)
        {
            left.Right = Merge(left.Right, right);
            Update(left);
            return left;
        }

        right.Left = Merge(left, right.Left);
        Update(right);
        return right;
    }

    private void Split(Node? node, int at, out Node? left, out Node? right)
    {
        if (node == null)
        {
            left = null;
            right = null;
            return;
        }

        var leftLength = Len(node.Left);
        if (at <= leftLength)
        {
            Split(node.Left, at, out left, out var inner);
            node.Left = inner;
            Update(node);
            right = node;
            return;
        }

        var pieceEnd = leftLength + node.Piece.Length;
        if (at >= pieceEnd)
        {
            Split(node.Right, at - pieceEnd, out var inner, out right);
            node.Right = inner;
            Update(node);
            left = node;
            return;
        }

        var cut = at - leftLength;
        var tail = node.Right;
        var second = SubPiece(node.Piece, cut, node.Piece.Length - cut);
        node.Piece = SubPiece(node.Piece, 0, cut);
        node.Right = null;
        Update(node);
        left = node;
        right = Merge(NewNode(second), tail);
    }

    private Piece SubPiece(Piece piece, int offset, int length)
    {
        var start = piece.Start + offset;
        return new Piece(piece.Buffer, start, length, CountBreaks(piece.Buffer, start, length));
    }

    private int CountBreaks(PieceBuffer buffer, int start, int length) =>
        BreaksThrough(buffer, start + length) - BreaksThrough(buffer, start);

    private int BreaksThrough(PieceBuffer buffer, int offset)
    {
        if (buffer == PieceBuffer.Original)
        {
            var found = Array.BinarySearch(_originalBreaks, offset);
            return found >= 0 ? found + 1 : ~found;
        }

        var lo = 0;
        var hi = _addBreaks.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_addBreaks[mid] <= offset) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private int BreakEnd(PieceBuffer buffer, int index) =>
        buffer == PieceBuffer.Original ? _originalBreaks[index] : _addBreaks[index];

    /// <summary>The offsets just past each line terminator in <paramref name="text"/>, shifted into
    /// the buffer it is being appended to. A CRLF is one terminator; a lone CR is one too.</summary>
    private static int[] BreakOffsets(string text, int bufferStart)
    {
        var offsets = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            offsets.Add(bufferStart + i + 1);
        }
        return offsets.ToArray();
    }

    private int LineStartOffset(int lineIndex)
    {
        if (lineIndex <= 0) return 0;

        var remaining = lineIndex - 1;
        var offset = 0;
        var node = _root;
        while (node != null)
        {
            var leftBreaks = Breaks(node.Left);
            if (remaining < leftBreaks)
            {
                node = node.Left;
                continue;
            }

            remaining -= leftBreaks;
            offset += Len(node.Left);
            if (remaining < node.Piece.Breaks)
            {
                var index = BreaksThrough(node.Piece.Buffer, node.Piece.Start) + remaining;
                return offset + BreakEnd(node.Piece.Buffer, index) - node.Piece.Start;
            }

            remaining -= node.Piece.Breaks;
            offset += node.Piece.Length;
            node = node.Right;
        }

        return offset;
    }

    private int LineContentLength(int lineIndex)
    {
        var start = LineStartOffset(lineIndex);
        if (lineIndex + 1 >= LineCount) return Length - start;

        var next = LineStartOffset(lineIndex + 1);
        var terminator = next - start >= 2 && CharAt(next - 1) == '\n' && CharAt(next - 2) == '\r' ? 2 : 1;
        return next - start - terminator;
    }

    private int OffsetOf(TextPosition position)
    {
        var clamped = Clamp(position);
        return LineStartOffset(clamped.Line.Value - 1) + clamped.Column.Value;
    }

    private TextPosition PositionAt(int offset)
    {
        var target = Math.Clamp(offset, 0, Length);
        var breaks = 0;
        var remaining = target;
        var node = _root;
        while (node != null)
        {
            var leftLength = Len(node.Left);
            if (remaining <= leftLength)
            {
                node = node.Left;
                continue;
            }

            breaks += Breaks(node.Left);
            remaining -= leftLength;
            if (remaining <= node.Piece.Length)
            {
                breaks += CountBreaks(node.Piece.Buffer, node.Piece.Start, remaining);
                break;
            }

            breaks += node.Piece.Breaks;
            remaining -= node.Piece.Length;
            node = node.Right;
        }

        return TextPosition.At(breaks + 1, target - LineStartOffset(breaks));
    }

    private char CharAt(int offset)
    {
        var remaining = offset;
        var node = _root;
        while (node != null)
        {
            var leftLength = Len(node.Left);
            if (remaining < leftLength)
            {
                node = node.Left;
                continue;
            }

            remaining -= leftLength;
            if (remaining < node.Piece.Length)
            {
                var at = node.Piece.Start + remaining;
                return node.Piece.Buffer == PieceBuffer.Original ? _original[at] : _add[at];
            }

            remaining -= node.Piece.Length;
            node = node.Right;
        }

        throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset is past the end of the document.");
    }

    private string SliceOffsets(int from, int to)
    {
        if (to <= from) return string.Empty;

        var text = new StringBuilder(to - from);
        AppendRange(text, _root, from, to);
        return text.ToString();
    }

    private void AppendRange(StringBuilder text, Node? node, int from, int to)
    {
        if (node == null || to <= from) return;

        var leftLength = Len(node.Left);
        if (from < leftLength)
            AppendRange(text, node.Left, from, Math.Min(to, leftLength));

        var pieceEnd = leftLength + node.Piece.Length;
        if (from < pieceEnd && to > leftLength)
        {
            var start = node.Piece.Start + Math.Max(from, leftLength) - leftLength;
            var length = Math.Min(to, pieceEnd) - Math.Max(from, leftLength);
            if (node.Piece.Buffer == PieceBuffer.Original) text.Append(_original, start, length);
            else text.Append(_add, start, length);
        }

        if (to > pieceEnd)
            AppendRange(text, node.Right, Math.Max(from, pieceEnd) - pieceEnd, to - pieceEnd);
    }
}
