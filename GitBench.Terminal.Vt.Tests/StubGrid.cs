using System.Diagnostics.CodeAnalysis;
namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The state a terminal is in before anything has been fed to it, for tests of the snapshot format
/// that have no engine in the way.
/// </summary>
public static class Reset
{
    public static TerminalModes Modes { get; } = new(
        ApplicationCursorKeys: false,
        ApplicationKeypad: false,
        AutoWrap: true,
        AlternateScreen: false,
        AlternateScroll: true,
        BracketedPaste: false,
        FocusReporting: false,
        SynchronizedOutput: false,
        MouseTracking: MouseTracking.Off,
        MouseEncoding: MouseEncoding.X10,
        KeyboardProtocolFlags: 0,
        ModifyOtherKeys: 0);

    public static TerminalState State { get; } = new(
        new TerminalCursor(0, 0, Visible: true, Shape: CursorShape.Block, Blinking: true),
        Modes,
        Title: string.Empty,
        IconTitle: string.Empty);
}

/// <summary>
/// A grid built by hand, so the snapshot format can be tested without an engine in the way. That
/// this is twenty lines is itself a result: a grid surface a test cannot fake is a grid surface a
/// second engine will struggle to implement.
/// </summary>
public sealed class StubGrid : ITerminalGrid
{
    readonly TerminalCell[][] rows;

    StubGrid(TerminalSize size)
    {
        Size = size;
        rows = Enumerable.Range(0, size.Rows)
            .Select(_ => Enumerable.Repeat(TerminalCell.Blank, size.Columns).ToArray())
            .ToArray();
    }

    public static StubGrid Of(int columns, int rows) => new(new TerminalSize(columns, rows));

    public TerminalSize Size { get; }

    public int ScrollbackRows => 0;

    public void CopyRow(int row, Span<TerminalCell> destination) => rows[row].CopyTo(destination);

    public bool ContinuesPreviousRow(int row) => false;

    readonly Dictionary<int, string> links = new();

    public bool TryGetHyperlink(HyperlinkId id, [NotNullWhen(true)] out TerminalHyperlink? link)
    {
        link = links.TryGetValue(id.Value, out var uri) ? new TerminalHyperlink(uri) : null;
        return link is not null;
    }

    /// <summary>Marks a stretch of one row as a link, the way an engine's OSC 8 handler would.</summary>
    public HyperlinkId Link(int column, int row, int count, string uri)
    {
        var id = new HyperlinkId(links.Count + 1);
        links[id.Value] = uri;

        for (var i = 0; i < count; i++)
            rows[row][column + i] = rows[row][column + i] with { Hyperlink = id };

        return id;
    }

    public void Write(int column, int row, string text)
    {
        foreach (var rune in text.EnumerateRunes())
            rows[row][column++] = TerminalCell.Blank with { Rune = rune };
    }

    public void WriteWide(int column, int row, Rune rune)
    {
        rows[row][column] = TerminalCell.Blank with { Rune = rune, Width = CellWidth.WideLeader };
        rows[row][column + 1] = TerminalCell.Blank with { Rune = new Rune(' '), Width = CellWidth.WideTrailer };
    }

    public void Put(int column, int row, Rune rune) => rows[row][column] = TerminalCell.Blank with { Rune = rune };

    public void Paint(int column, int row, int count, CellStyle style)
    {
        for (var i = 0; i < count; i++)
        {
            rows[row][column + i] = rows[row][column + i] with
            {
                Foreground = style.Foreground,
                Background = style.Background,
                Attributes = style.Attributes,
            };
        }
    }
}
