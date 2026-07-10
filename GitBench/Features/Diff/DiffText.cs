namespace GitBench.Features.Diff;

/// <summary>
/// Shared text shaping for the diff body. Tab expansion lives here (rather than privately in
/// the renderer) because syntax highlighting must expand tabs the exact same way before
/// tokenizing — token columns are produced in tab-expanded space so they align 1:1 with the
/// rendered line text. Any divergence here would slide highlight colors off their glyphs.
/// </summary>
internal static class DiffText
{
    private static readonly string TabReplacement = new(' ', DiffOptions.TabWidth);

    public static string ExpandTabs(string s)
    {
        if (s.IndexOf('\t') < 0) return s;
        return s.Replace("\t", TabReplacement);
    }

    // East-Asian display width in monospace cells: wide/fullwidth code points take two cells,
    // everything else one. Horizontal extents are sized from this so a spaceless CJK line —
    // whose glyphs run about twice a Latin cell — can be scrolled fully into view instead of
    // being cut off at the right edge. Two cells slightly over-estimates the fallback font's real
    // advance, which only ever leaves a little slack — it never clips.
    public static int VisualCells(string text)
    {
        var cells = 0;
        var i = 0;
        while (i < text.Length) cells += StepCells(text, ref i);
        return cells;
    }

    /// <summary>Cells occupied by <c>text[0..charIndex)</c> — the column a caret at that offset
    /// sits in. The x of a text position is <c>origin + CellsBefore(...) * monoAdvance</c>.</summary>
    public static int CellsBefore(string text, int charIndex)
    {
        var limit = Math.Clamp(charIndex, 0, text.Length);
        var cells = 0;
        var i = 0;
        while (i < limit) cells += StepCells(text, ref i);
        return cells;
    }

    /// <summary>
    /// The character offset nearest the given column, for turning a pointer x into a caret. A
    /// column landing in a glyph's leading half snaps before it and its trailing half after, so
    /// clicks read as "between characters"; a surrogate pair or a two-cell CJK glyph is never
    /// split.
    /// </summary>
    public static int CharIndexAtCell(string text, float cell)
    {
        if (cell <= 0f) return 0;
        var cells = 0;
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var width = StepCells(text, ref i);
            if (cell < cells + width / 2f) return start;
            cells += width;
        }
        return text.Length;
    }

    // Cells of the code point at i, advancing i past it (surrogate pairs count as one glyph).
    private static int StepCells(string text, ref int i)
    {
        var c = text[i];
        int cp;
        if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
        {
            cp = char.ConvertToUtf32(c, text[i + 1]);
            i += 2;
        }
        else
        {
            cp = c;
            i++;
        }
        return IsWideCodePoint(cp) ? 2 : 1;
    }

    private static bool IsWideCodePoint(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) ||   // Hangul Jamo
        (cp >= 0x2E80 && cp <= 0x303E) ||   // CJK Radicals … CJK Symbols and Punctuation
        (cp >= 0x3041 && cp <= 0x33FF) ||   // Hiragana … Enclosed CJK Letters and Months
        (cp >= 0x3400 && cp <= 0x4DBF) ||   // CJK Unified Ideographs Extension A
        (cp >= 0x4E00 && cp <= 0x9FFF) ||   // CJK Unified Ideographs
        (cp >= 0xA960 && cp <= 0xA97F) ||   // Hangul Jamo Extended-A
        (cp >= 0xAC00 && cp <= 0xD7A3) ||   // Hangul Syllables
        (cp >= 0xF900 && cp <= 0xFAFF) ||   // CJK Compatibility Ideographs
        (cp >= 0xFF00 && cp <= 0xFF60) ||   // Fullwidth Forms (halfwidth katakana 0xFF61+ stay narrow)
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||   // Fullwidth signs
        (cp >= 0x20000 && cp <= 0x3FFFD);   // Supplementary ideographic plane (CJK Ext B+)
}
