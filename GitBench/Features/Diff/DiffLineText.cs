namespace GitBench.Features.Diff;

/// <summary>A UTF-16 offset into a line's raw text — the file's own characters, a tab counting as
/// the one character it is. What has to reach the clipboard, the assistant, or a patch.</summary>
internal readonly record struct RawColumn(int Value);

/// <summary>
/// A UTF-16 offset into a line's tab-expanded text — the space the painter, the hit-test, syntax
/// spans and intra-line emphasis all count in. Distinct from <see cref="RawColumn"/> because the
/// two coincide only on lines with no tabs, so a swap reads perfectly fine at the call site and
/// shows up as mangled indentation somewhere else entirely.
/// </summary>
internal readonly record struct ExpandedColumn(int Value) : IComparable<ExpandedColumn>
{
    public int CompareTo(ExpandedColumn other) => Value.CompareTo(other.Value);

    public static bool operator <(ExpandedColumn a, ExpandedColumn b) => a.Value < b.Value;
    public static bool operator >(ExpandedColumn a, ExpandedColumn b) => a.Value > b.Value;
    public static bool operator <=(ExpandedColumn a, ExpandedColumn b) => a.Value <= b.Value;
    public static bool operator >=(ExpandedColumn a, ExpandedColumn b) => a.Value >= b.Value;
}

/// <summary>
/// Which raw offset an expanded column that landed inside the run of spaces a tab expanded into
/// resolves to. A tab is one character, so there is no offset between its spaces to return:
/// <see cref="Before"/> lands on the tab and <see cref="After"/> past it. Slicing takes
/// <see cref="Before"/> for the start and <see cref="After"/> for the end, which is what makes a
/// selection covering part of a tab copy the whole tab rather than dropping it.
/// </summary>
internal enum TabEdge
{
    Before,
    After,
}

/// <summary>
/// One line of a diff in both spaces at once: the raw file text and its tab-expanded rendering,
/// plus the mapping between their column spaces. Rendering, hit-testing and highlighting work in
/// expanded columns; anything that leaves the app as text takes <see cref="RawSlice"/>, so tabs
/// survive the trip to the clipboard.
/// </summary>
internal sealed record DiffLineText
{
    private readonly bool _hasTabs;

    private DiffLineText(string raw, string expanded, bool hasTabs)
    {
        Raw = raw;
        Expanded = expanded;
        _hasTabs = hasTabs;
    }

    public static DiffLineText Of(string raw)
    {
        var hasTabs = raw.IndexOf('\t') >= 0;
        return new DiffLineText(raw, hasTabs ? DiffText.ExpandTabs(raw) : raw, hasTabs);
    }

    public string Raw { get; }
    public string Expanded { get; }

    /// <summary>The column just past the last character, where a whole-line selection ends.</summary>
    public ExpandedColumn End => new(Expanded.Length);

    public RawColumn ToRaw(ExpandedColumn column, TabEdge edge)
    {
        var target = Math.Clamp(column.Value, 0, Expanded.Length);
        if (!_hasTabs) return new RawColumn(target);

        var expanded = 0;
        for (var raw = 0; raw < Raw.Length; raw++)
        {
            var width = Raw[raw] == '\t' ? DiffOptions.TabWidth : 1;
            if (target < expanded + width)
                return new RawColumn(target == expanded || edge == TabEdge.Before ? raw : raw + 1);
            expanded += width;
        }
        return new RawColumn(Raw.Length);
    }

    public ExpandedColumn ToExpanded(RawColumn column)
    {
        var limit = Math.Clamp(column.Value, 0, Raw.Length);
        if (!_hasTabs) return new ExpandedColumn(limit);

        var expanded = 0;
        for (var i = 0; i < limit; i++) expanded += Raw[i] == '\t' ? DiffOptions.TabWidth : 1;
        return new ExpandedColumn(expanded);
    }

    /// <summary>The raw text under an expanded-column range, widened over any tab the range only
    /// partly covers. Empty when the range covers nothing.</summary>
    public string RawSlice(ExpandedColumn from, ExpandedColumn to)
    {
        if (to <= from) return string.Empty;
        var start = ToRaw(from, TabEdge.Before).Value;
        var end = ToRaw(to, TabEdge.After).Value;
        return end <= start ? string.Empty : Raw[start..end];
    }
}
