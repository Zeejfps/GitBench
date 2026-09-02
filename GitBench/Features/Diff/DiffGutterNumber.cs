using System.Globalization;

namespace GitBench.Features.Diff;

/// <summary>A 1-based line number in one side of the diff's file — what the gutter shows, what a
/// reader cites, and what an outline is indexed by.</summary>
internal readonly record struct FileLine(int Value) : IComparable<FileLine>
{
    public int CompareTo(FileLine other) => Value.CompareTo(other.Value);

    public static bool operator <(FileLine a, FileLine b) => a.Value < b.Value;
    public static bool operator >(FileLine a, FileLine b) => a.Value > b.Value;
    public static bool operator <=(FileLine a, FileLine b) => a.Value <= b.Value;
    public static bool operator >=(FileLine a, FileLine b) => a.Value >= b.Value;
}

/// <summary>
/// A 0-based index into a diff's flattened row stream. Distinct from <see cref="FileLine"/> because
/// the stream also carries banners, hunk separators and tears, and drops the lines a collapsed fold
/// or an unexpanded gap hides — so row 40 is not line 40, and the two agree often enough at the top
/// of a file for a swap to look right until it doesn't. <see cref="DiffRowSet"/> owns the mapping;
/// there is no conversion.
/// </summary>
internal readonly record struct RowIndex(int Value) : IComparable<RowIndex>
{
    public int CompareTo(RowIndex other) => Value.CompareTo(other.Value);

    public static bool operator <(RowIndex a, RowIndex b) => a.Value < b.Value;
    public static bool operator >(RowIndex a, RowIndex b) => a.Value > b.Value;
    public static bool operator <=(RowIndex a, RowIndex b) => a.Value <= b.Value;
    public static bool operator >=(RowIndex a, RowIndex b) => a.Value >= b.Value;
}

/// <summary>
/// One line-number gutter cell in both forms at once: the <see cref="FileLine"/> it stands for and
/// the digits the painter draws. Rows carry it instead of the formatted text alone, so nothing has
/// to recover a line number by parsing a gutter back; rows carry it instead of the number alone, so
/// neither the painter nor the gutter-width measurement formats once per frame.
/// </summary>
/// <remarks>
/// <see cref="None"/> is the default value, so a side with no number here — the after side of a
/// removed line, the before side of an added one, either side of a banner — has exactly one
/// representation rather than an empty string standing in for a missing number.
/// </remarks>
internal readonly record struct DiffGutterNumber
{
    private readonly string? _text;

    private DiffGutterNumber(FileLine line, string text)
    {
        Line = line;
        _text = text;
    }

    public static DiffGutterNumber None => default;

    public static DiffGutterNumber Of(FileLine? line) =>
        line is { } l
            ? new DiffGutterNumber(l, l.Value.ToString(CultureInfo.InvariantCulture))
            : None;

    public FileLine? Line { get; }

    /// <summary>The digits to draw, empty where this side has no number.</summary>
    public string Text => _text ?? string.Empty;
}
