using System.Globalization;
using System.Text;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// A frozen copy of everything <see cref="ITerminalGrid"/> and <see cref="TerminalState"/> expose,
/// and the text form the goldens are written in.
/// </summary>
/// <remarks>
/// <para>
/// The format is built to be read in a diff. Three ideas carry it:
/// </para>
/// <para>
/// <b>Separate planes.</b> The screen is written twice — once as text, once as one letter per cell
/// naming its style — so a colour change and a character change appear as different lines, and
/// both stay aligned to the column they happened in. Inline SGR would put a variable-length escape
/// in front of every run and destroy the column alignment that makes a screen diff readable.
/// </para>
/// <para>
/// <b>A legend instead of inline colour.</b> Styles are spelled out once at the bottom, sorted by
/// colour then attributes, so the plane stays one character wide and the legend's own diff is
/// minimal.
/// </para>
/// <para>
/// <b>Blank runs collapse.</b> A terminal screen is mostly empty. Collapsing runs of untouched
/// rows is what makes a twenty-frame golden a few hundred lines instead of tens of thousands, and
/// it turns "the output moved down two rows" into a two-line diff.
/// </para>
/// </remarks>
public sealed class GridSnapshot
{
    readonly TerminalCell[][] rows;
    readonly IReadOnlyList<int> continuations;

    GridSnapshot(
        string label,
        TerminalSize size,
        int scrollbackRows,
        TerminalState state,
        TerminalCell[][] rows,
        IReadOnlyList<int> continuations)
    {
        Label = label;
        Size = size;
        ScrollbackRows = scrollbackRows;
        State = state;
        this.rows = rows;
        this.continuations = continuations;
    }

    public string Label { get; }

    public TerminalSize Size { get; }

    public int ScrollbackRows { get; }

    public TerminalState State { get; }

    /// <summary>The grid row the first captured row corresponds to: negative when history exists.</summary>
    int FirstRow => -ScrollbackRows;

    public static GridSnapshot Capture(ITerminalEngine engine, string label) =>
        Capture(engine.Grid, engine.State, label);

    public static GridSnapshot Capture(ITerminalGrid grid, TerminalState state, string label)
    {
        var size = grid.Size;
        var history = grid.ScrollbackRows;
        var rows = new TerminalCell[history + size.Rows][];
        var continuations = new List<int>();

        for (var row = -history; row < size.Rows; row++)
        {
            var cells = new TerminalCell[size.Columns];
            grid.CopyRow(row, cells);
            rows[row + history] = cells;
            if (grid.ContinuesPreviousRow(row))
                continuations.Add(row);
        }

        return new GridSnapshot(label, size, history, state, rows, continuations);
    }

    /// <summary>The line-per-row text a golden file is made of.</summary>
    public IEnumerable<string> ToLines()
    {
        var styles = StyleLegend.Build(rows);

        yield return $"# {Label}";
        yield return $"size {Size}";
        yield return $"alt {(State.Modes.AlternateScreen ? "on" : "off")}";
        yield return $"cursor {DescribeCursor(State.Cursor)}";
        yield return $"scrollback {ScrollbackRows}";
        yield return $"title {Quote(State.Title)}";
        yield return $"icon {Quote(State.IconTitle)}";
        yield return $"modes {DescribeModes(State.Modes)}";
        yield return $"continues {(continuations.Count == 0 ? "none" : string.Join(' ', continuations.Select(row => Name('r', row))))}";

        yield return "text";
        foreach (var line in Plane(TextRow, "~blank~", 'r'))
            yield return line;

        yield return "style";
        foreach (var line in Plane(row => styles.Row(rows[row - FirstRow]), "~default~", 's'))
            yield return line;

        if (rows.Any(row => row.Any(cell => cell.Width != CellWidth.Single)))
        {
            yield return "width";
            foreach (var line in Plane(WidthRow, "~narrow~", 'w'))
                yield return line;
        }

        yield return "legend";
        foreach (var entry in styles.Entries)
            yield return $"  {entry}";

        var odd = Oddities().ToList();
        if (odd.Count > 0)
        {
            yield return "runes";
            foreach (var line in odd)
                yield return $"  {line}";
        }
    }

    public string ToText() => string.Join('\n', ToLines()) + '\n';

    /// <summary>
    /// Writes one plane, collapsing runs of rows that carry nothing. Each row keeps its index, so
    /// a diff names the row that moved rather than just showing that something did.
    /// </summary>
    IEnumerable<string> Plane(Func<int, string> render, string emptyToken, char prefix)
    {
        var row = FirstRow;
        var end = Size.Rows;

        while (row < end)
        {
            var rendered = render(row);
            if (rendered.Length > 0)
            {
                yield return $"  {Name(prefix, row)} |{rendered}|";
                row++;
                continue;
            }

            var last = row;
            while (last + 1 < end && render(last + 1).Length == 0)
                last++;

            yield return last == row
                ? $"  {Name(prefix, row)} {emptyToken}"
                : $"  {Name(prefix, row)}-{Name(prefix, last)} {emptyToken}";
            row = last + 1;
        }
    }

    /// <summary>
    /// A row's name. History is written with a minus, so the boundary between what has scrolled
    /// away and what is on screen is visible at a glance and does not move when history grows.
    /// </summary>
    static string Name(char prefix, int row) => row < 0 ? $"{prefix}-{-row:00}" : $"{prefix}{row:00}";

    /// <summary>
    /// A row of text, right-trimmed of cells that hold nothing at all. Trimming keeps lines short
    /// without disturbing column alignment, which is left-anchored; a space with a background
    /// colour is not blank and survives the trim.
    /// </summary>
    string TextRow(int row)
    {
        var cells = rows[row - FirstRow];
        var width = TrimmedWidth(cells);
        var text = new StringBuilder(width);

        for (var column = 0; column < width; column++)
        {
            var cell = cells[column];
            text.Append(cell.Width == CellWidth.WideTrailer ? ' ' : Printable(cell.Rune));
        }

        return text.ToString();
    }

    string WidthRow(int row)
    {
        var cells = rows[row - FirstRow];
        var width = TrimmedWidth(cells);
        if (cells.Take(width).All(cell => cell.Width == CellWidth.Single))
            return string.Empty;

        return string.Concat(cells.Take(width).Select(cell => cell.Width switch
        {
            CellWidth.WideLeader => 'W',
            CellWidth.WideTrailer => '-',
            _ => '.',
        }));
    }

    /// <summary>Cells whose rune has no glyph, listed so the text plane can stay column-aligned.</summary>
    IEnumerable<string> Oddities()
    {
        for (var index = 0; index < rows.Length; index++)
        {
            for (var column = 0; column < rows[index].Length; column++)
            {
                var rune = rows[index][column].Rune;
                if (!IsPrintable(rune))
                    yield return $"{Name('r', index + FirstRow)}c{column:000} U+{rune.Value:X4}";
            }
        }
    }

    internal static int TrimmedWidth(TerminalCell[] cells)
    {
        var width = cells.Length;
        while (width > 0 && cells[width - 1].IsBlank())
            width--;
        return width;
    }

    const char NonPrintable = '¤';

    static bool IsPrintable(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned);

    static char Printable(Rune rune)
    {
        if (!IsPrintable(rune))
            return NonPrintable;

        return rune.IsBmp ? (char)rune.Value : NonPrintable;
    }

    static string DescribeCursor(TerminalCursor cursor) =>
        $"col={cursor.Column} row={cursor.Row} {(cursor.Visible ? "visible" : "hidden")} "
        + $"shape={cursor.Shape.ToString().ToLowerInvariant()} {(cursor.Blinking ? "blink" : "steady")}";

    static string DescribeModes(TerminalModes modes)
    {
        var parts = new List<string>();
        if (modes.AutoWrap) parts.Add("autowrap");
        if (modes.BracketedPaste) parts.Add("bracketed-paste");
        if (modes.ApplicationCursorKeys) parts.Add("app-cursor");
        if (modes.ApplicationKeypad) parts.Add("app-keypad");
        if (modes.FocusReporting) parts.Add("focus");
        if (modes.SynchronizedOutput) parts.Add("sync");
        parts.Add($"mouse={Lower(modes.MouseTracking)}/{Lower(modes.MouseEncoding)}");
        parts.Add($"kitty={(modes.KeyboardProtocolFlags == 0 ? "none" : modes.KeyboardProtocolFlags.ToString(CultureInfo.InvariantCulture))}");
        if (modes.ModifyOtherKeys != 0) parts.Add($"modify-other-keys={modes.ModifyOtherKeys}");
        return string.Join(' ', parts);
    }

    static string Lower(MouseTracking tracking) => tracking.ToString().ToLowerInvariant();

    static string Lower(MouseEncoding encoding) => encoding.ToString().ToLowerInvariant();

    static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is '"' or '\\')
                quoted.Append('\\').Append((char)rune.Value);
            else if (IsPrintable(rune))
                quoted.Append(rune);
            else
                quoted.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:X4}");
        }

        return quoted.Append('"').ToString();
    }
}
