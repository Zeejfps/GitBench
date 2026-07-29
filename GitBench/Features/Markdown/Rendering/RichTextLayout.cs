using ZGF.Gui;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// One positioned slice of a run on a laid-out line. <paramref name="Start"/>/<paramref name="Length"/>
/// index the run's <see cref="RichTextRun.Text"/> in UTF-16 units; <paramref name="X"/> is the
/// segment's left edge relative to the line's origin (the first segment of a line sits at 0);
/// <paramref name="Width"/> is the measured width of the slice in the run's style. Slices tile
/// the run text the way <see cref="TextWrapper.WrapRanges"/> ranges tile their input: a
/// soft-wrapped line ends where the next begins — spaces at the break stay on the segment they
/// follow — while a '\n' appears in no segment.
/// </summary>
internal readonly record struct RichTextSegment(int RunIndex, int Start, int Length, float X, float Width);

/// <summary>
/// One visual line: its segments left-to-right, the line's total advance
/// (<paramref name="Width"/>), and its <paramref name="Height"/> — the tallest line height among
/// the styles of the runs present on the line (a forced-empty line keeps the height of the run
/// whose '\n' produced it). Lines with no segments occur only from consecutive or trailing
/// newlines.
/// </summary>
internal sealed record RichTextLine(IReadOnlyList<RichTextSegment> Segments, float Width, float Height);

/// <summary>
/// A finished layout: lines top-to-bottom, <paramref name="Height"/> the sum of line heights,
/// <paramref name="MaxLineWidth"/> the widest line — the natural (unwrapped-or-as-wrapped) width
/// the view reports as its intrinsic measure.
/// </summary>
internal sealed record RichTextLayoutResult(IReadOnlyList<RichTextLine> Lines, float Height, float MaxLineWidth);

/// <summary>
/// The run-aware wrap engine behind <see cref="RichTextView"/> and (later) table cells: greedy
/// UAX-14-lite wrap over styled runs, measuring each slice with its own run's
/// <see cref="TextStyle"/> through the canvas.
/// <para>
/// Break behavior is <see cref="TextWrapper"/>'s, and for single-style input the line splits must
/// match <see cref="TextWrapper.WrapRanges"/> exactly (the shared test corpus is the gate):
/// breaks at spaces and after separator punctuation (<c>/ \ - _ . :</c>), CJK breaks between
/// code points, kinsoku prohibitions, and code-point splitting for a chunk with no break
/// opportunity that is wider than the line. A run boundary is <b>not</b> a break opportunity by
/// itself — adjacent runs break at their seam only where the concatenated text would break
/// anyway (a space before the seam, a separator ending the left run, CJK on either side).
/// A non-positive <paramref name="maxWidth"/> disables wrapping ('\n' still breaks). An empty
/// run list (or runs with no text at all) produces zero lines and zero height.
/// </para>
/// <para>
/// Implementation: the runs are viewed as one concatenated text (materialized once per layout),
/// so break decisions see exactly the character stream <see cref="TextWrapper"/> would — a
/// surrogate pair split across two runs still reads as one code point, and an empty run is
/// invisible. Widths, by contrast, are always taken per run slice with that run's own style, which
/// degenerates to the reference's single measurement calls when there is one run.
/// </para>
/// </summary>
internal static class RichTextLayout
{
    private static readonly RichTextLayoutResult EmptyResult = new(Array.Empty<RichTextLine>(), 0f, 0f);

    /// <summary>Lays <paramref name="runs"/> out into lines of positioned segments no wider than
    /// <paramref name="maxWidth"/>, measuring through <paramref name="canvas"/>.</summary>
    public static RichTextLayoutResult Layout(ICanvas canvas, IReadOnlyList<RichTextRun> runs, float maxWidth)
    {
        if (!TryCreateMap(canvas, runs, out var map))
            return EmptyResult;

        var total = map.Text.Length;

        // Forced breaks first, mirroring TextWrapper.WrapRanges: a '\n' terminates a line and
        // belongs to none, so consecutive or trailing newlines yield empty (zero-length) ranges.
        var ranges = new List<(int Start, int End)>();
        var lineStart = 0;
        for (var i = 0; i <= total; i++)
        {
            if (i != total && map.Text[i] != '\n')
                continue;
            WrapForcedLine(in map, lineStart, i, maxWidth, ranges);
            lineStart = i + 1;
        }

        var lines = new RichTextLine[ranges.Count];
        var height = 0f;
        var maxLineWidth = 0f;
        for (var i = 0; i < ranges.Count; i++)
        {
            var line = BuildLine(in map, ranges[i].Start, ranges[i].End);
            lines[i] = line;
            height += line.Height;
            if (line.Width > maxLineWidth)
                maxLineWidth = line.Width;
        }

        return new RichTextLayoutResult(lines, height, maxLineWidth);
    }

    /// <summary>
    /// The width of the widest <i>unbreakable chunk</i> in <paramref name="runs"/> — the
    /// narrowest width <see cref="Layout"/> can be given without ever force-splitting between
    /// code points, which is exactly a table cell's min-content width (see
    /// <see cref="TableLayout"/>). The chunk scan (<see cref="ScanChunkEnd"/>) is shared with
    /// <see cref="WrapForcedLine"/>, running over the same concatenated view: spaces separate
    /// chunks and belong to none, a chunk extends
    /// while no break opportunity exists between consecutive code points
    /// (<see cref="BreakAllowedBetween"/> — separator punctuation ends its chunk, CJK breaks
    /// per code point, kinsoku glues punctuation), a '\n' terminates a chunk, and a run seam is
    /// not a break by itself. Every chunk is measured per run slice in that run's own style.
    /// </summary>
    public static float MeasureWidestChunk(ICanvas canvas, IReadOnlyList<RichTextRun> runs)
    {
        if (!TryCreateMap(canvas, runs, out var map))
            return 0f;

        var text = map.Text;
        var end = text.Length;
        var widest = 0f;
        var i = 0;
        while (i < end)
        {
            if (text[i] is ' ' or '\n')
            {
                i++;
                continue;
            }

            var chunkStart = i;
            i = ScanChunkEnd(text, i, end);

            var width = map.MeasureRange(chunkStart, i);
            if (width > widest)
                widest = width;
        }

        return widest;
    }

    /// <summary>Advances from <paramref name="start"/> (which must sit on a non-space character)
    /// to the end of the unbreakable chunk beginning there: the chunk extends while no break
    /// opportunity exists between consecutive code points (<see cref="BreakAllowedBetween"/>) and
    /// stops before a space or '\n' (the wrap path's forced lines contain no '\n', so that guard
    /// is inert there).</summary>
    private static int ScanChunkEnd(string text, int start, int end)
    {
        var i = start;
        var prev = ReadCodePoint(text, ref i, end);
        while (i < end && text[i] != ' ' && text[i] != '\n')
        {
            var next = PeekCodePoint(text, i, end, out var nextLen);
            if (BreakAllowedBetween(prev, next))
                break;
            prev = next;
            i += nextLen;
        }
        return i;
    }

    /// <summary>Builds the concatenated <see cref="RunMap"/> over <paramref name="runs"/>, or
    /// returns false when the runs carry no text at all.</summary>
    private static bool TryCreateMap(ICanvas canvas, IReadOnlyList<RichTextRun> runs, out RunMap map)
    {
        var starts = new int[runs.Count + 1];
        var total = 0;
        for (var i = 0; i < runs.Count; i++)
        {
            starts[i] = total;
            total += runs[i].Text.Length;
        }
        starts[runs.Count] = total;

        if (total == 0)
        {
            map = default;
            return false;
        }

        map = new RunMap(canvas, runs, starts, Concatenate(runs, total));
        return true;
    }

    /// <summary>
    /// Greedy wrap of one forced line, ported from <see cref="TextWrapper"/>'s
    /// <c>WrapLineRanges</c> — same chunking, same accumulation, same over-wide handling — with
    /// every width taken through <see cref="RunMap.MeasureRange"/> so each slice is measured in
    /// its own run's style (the reference's <c>spaces * spaceWidth</c> becomes "measure the space
    /// characters where they live", identical for a single style). The emitted ranges are global
    /// indices into the concatenated text and tile it exactly like the reference's ranges.
    /// </summary>
    private static void WrapForcedLine(
        in RunMap map, int start, int end, float maxWidth, List<(int Start, int End)> output)
    {
        if (start >= end || maxWidth <= 0f || map.MeasureRange(start, end) <= maxWidth)
        {
            output.Add((start, end));
            return;
        }

        var text = map.Text;
        var lineStart = start;
        var lineWidth = 0f;
        var lineHasContent = false;

        var i = start;
        while (i < end)
        {
            var spacesStart = i;
            while (i < end && text[i] == ' ')
                i++;
            if (i >= end)
                break;

            var chunkStart = i;
            i = ScanChunkEnd(text, i, end);

            var chunkWidth = map.MeasureRange(chunkStart, i);
            var sep = map.MeasureRange(spacesStart, chunkStart);

            if (chunkWidth > maxWidth)
            {
                // Mirrors the reference: an unbreakable over-wide chunk starts a fresh line and is
                // split between code points, kinsoku still honored.
                if (lineHasContent)
                {
                    output.Add((lineStart, chunkStart));
                    lineStart = chunkStart;
                    lineWidth = 0f;
                }
                else
                {
                    lineWidth += sep;
                }

                var j = chunkStart;
                var before = -1;
                while (j < i)
                {
                    var cur = PeekCodePoint(text, j, end, out var len);
                    var w = map.MeasureRange(j, j + len);
                    if (j > lineStart && lineWidth + w > maxWidth && BreakAllowedHere(before, cur))
                    {
                        output.Add((lineStart, j));
                        lineStart = j;
                        lineWidth = 0f;
                    }
                    lineWidth += w;
                    before = cur;
                    j += len;
                }

                lineHasContent = true;
            }
            else if (!lineHasContent)
            {
                lineWidth += sep + chunkWidth;
                lineHasContent = true;
            }
            else if (lineWidth + sep + chunkWidth <= maxWidth)
            {
                lineWidth += sep + chunkWidth;
            }
            else
            {
                output.Add((lineStart, chunkStart));
                lineStart = chunkStart;
                lineWidth = chunkWidth;
            }
        }

        output.Add((lineStart, end));
    }

    /// <summary>Materializes a global line range as positioned per-run segments. Empty runs inside
    /// the range contribute no segment; an empty range is a forced-empty line and takes the line
    /// height of the run whose '\n' produced it.</summary>
    private static RichTextLine BuildLine(in RunMap map, int start, int end)
    {
        if (start >= end)
        {
            // The producing '\n' is the character just before the line; a leading empty line has
            // none, so it takes the terminating '\n' at its own (start) position instead.
            var anchor = start > 0 ? start - 1 : start;
            var style = map.Runs[map.RunAt(anchor)].Style;
            return new RichTextLine(
                Array.Empty<RichTextSegment>(), 0f, map.Canvas.MeasureTextLineHeight(style));
        }

        var segments = new List<RichTextSegment>(2);
        var x = 0f;
        var height = 0f;
        var k = map.RunAt(start);
        var g = start;
        while (g < end)
        {
            var runEnd = map.Starts[k + 1];
            if (runEnd <= g)
            {
                k++;
                continue;
            }

            var run = map.Runs[k];
            var sliceStart = g - map.Starts[k];
            var sliceLength = Math.Min(end, runEnd) - map.Starts[k] - sliceStart;
            var width = map.Canvas.MeasureTextWidth(run.Text.AsSpan(sliceStart, sliceLength), run.Style);
            segments.Add(new RichTextSegment(k, sliceStart, sliceLength, x, width));
            x += width;

            var h = map.Canvas.MeasureTextLineHeight(run.Style);
            if (h > height)
                height = h;

            g = Math.Min(end, runEnd);
            k++;
        }

        return new RichTextLine(segments, x, height);
    }

    private static string Concatenate(IReadOnlyList<RichTextRun> runs, int total)
    {
        if (runs.Count == 1)
            return runs[0].Text;

        return string.Create(total, runs, static (span, rs) =>
        {
            var offset = 0;
            for (var i = 0; i < rs.Count; i++)
            {
                rs[i].Text.AsSpan().CopyTo(span[offset..]);
                offset += rs[i].Text.Length;
            }
        });
    }

    /// <summary>The concatenated view over the runs: global character indices for break decisions,
    /// per-run slices for measurement.</summary>
    private readonly struct RunMap
    {
        public readonly ICanvas Canvas;
        public readonly IReadOnlyList<RichTextRun> Runs;
        public readonly int[] Starts; // Starts[k] = global start of run k; Starts[^1] = total length
        public readonly string Text;  // the runs' text concatenated

        public RunMap(ICanvas canvas, IReadOnlyList<RichTextRun> runs, int[] starts, string text)
        {
            Canvas = canvas;
            Runs = runs;
            Starts = starts;
            Text = text;
        }

        /// <summary>The run containing global character <paramref name="index"/>. Because "last run
        /// starting at or before the index" is only ambiguous for empty runs — which contain no
        /// character — the plain binary search always lands on the owning, non-empty run.</summary>
        public int RunAt(int index)
        {
            var lo = 0;
            var hi = Runs.Count - 1;
            while (lo < hi)
            {
                var mid = lo + (hi - lo + 1) / 2;
                if (Starts[mid] <= index)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Width of the global range [<paramref name="start"/>, <paramref name="end"/>),
        /// summed per run slice, each measured in its run's own style.</summary>
        public float MeasureRange(int start, int end)
        {
            if (start >= end)
                return 0f;

            var width = 0f;
            var k = RunAt(start);
            var g = start;
            while (g < end)
            {
                var runEnd = Starts[k + 1];
                if (runEnd <= g)
                {
                    k++;
                    continue;
                }

                var run = Runs[k];
                var sliceStart = g - Starts[k];
                var sliceLength = Math.Min(end, runEnd) - Starts[k] - sliceStart;
                width += Canvas.MeasureTextWidth(run.Text.AsSpan(sliceStart, sliceLength), run.Style);
                g = Math.Min(end, runEnd);
                k++;
            }

            return width;
        }
    }

    // ---------- code points and break classes, ported from TextWrapper ----------
    // The kinsoku/separator tables are private in the framework and must stay byte-in-sync with
    // TextWrapper's; the (large) wide-script table is public and reused directly.

    private static int ReadCodePoint(string s, ref int i, int end)
    {
        var cp = PeekCodePoint(s, i, end, out var len);
        i += len;
        return cp;
    }

    private static int PeekCodePoint(string s, int i, int end, out int len)
    {
        var c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < end && char.IsLowSurrogate(s[i + 1]))
        {
            len = 2;
            return char.ConvertToUtf32(c, s[i + 1]);
        }

        len = 1;
        return c;
    }

    private static bool BreakAllowedBetween(int before, int after)
    {
        if (IsNoBreakBefore(after)) return false;  // kinsoku: closing punctuation can't start a line
        if (IsNoBreakAfter(before)) return false;  // kinsoku: opening punctuation can't end a line
        return TextWrapper.IsWide(before) || TextWrapper.IsWide(after) || IsBreakAfter(before);
    }

    /// <summary>Whether a last-resort character break may fall between two code points: no break
    /// opportunity required, only the absence of a kinsoku prohibition. <paramref name="before"/>
    /// is -1 at the start of a chunk.</summary>
    private static bool BreakAllowedHere(int before, int after) =>
        before >= 0 && !IsNoBreakBefore(after) && !IsNoBreakAfter(before);

    /// <summary>Separators that permit a break on their trailing side, so paths, URLs and
    /// snake_case identifiers wrap at their natural boundaries.</summary>
    private static bool IsBreakAfter(int cp) => cp switch
    {
        '/' or '\\' or '-' or '_' or '.' or ':' => true,
        _ => false,
    };

    /// <summary>Characters that must not begin a wrapped line (closing punctuation, small kana).</summary>
    private static bool IsNoBreakBefore(int cp) => cp switch
    {
        ',' or '.' or '!' or '?' or ':' or ';' or ')' or ']' or '}' => true,
        0x3001 or 0x3002 or 0x3009 or 0x300B or 0x300D or 0x300F or 0x3011
            or 0x3015 or 0x3017 or 0x3019 or 0x301B => true,  // 、。〉》」』】〕〗〙〛
        0x3005 or 0x301C or 0x30FC => true,                   // 々〜ー
        0x2025 or 0x2026 => true,                             // ‥…
        0x3041 or 0x3043 or 0x3045 or 0x3047 or 0x3049 or 0x3063
            or 0x3083 or 0x3085 or 0x3087 or 0x308E or 0x3095 or 0x3096 => true,  // small hiragana
        0x30A1 or 0x30A3 or 0x30A5 or 0x30A7 or 0x30A9 or 0x30C3
            or 0x30E3 or 0x30E5 or 0x30E7 or 0x30EE or 0x30F5 or 0x30F6 => true,  // small katakana
        0xFF01 or 0xFF09 or 0xFF0C or 0xFF0E or 0xFF1A or 0xFF1B
            or 0xFF1F or 0xFF3D or 0xFF5D or 0xFF63 => true,  // ！），．：；？］｝｣
        _ => false,
    };

    /// <summary>Characters that must not end a line (opening punctuation).</summary>
    private static bool IsNoBreakAfter(int cp) => cp switch
    {
        '(' or '[' or '{' => true,
        0x3008 or 0x300A or 0x300C or 0x300E or 0x3010
            or 0x3014 or 0x3016 or 0x3018 or 0x301A => true,  // 〈《「『【〔〖〘〚
        0xFF08 or 0xFF3B or 0xFF5B or 0xFF62 => true,         // （［｛｢
        _ => false,
    };
}
