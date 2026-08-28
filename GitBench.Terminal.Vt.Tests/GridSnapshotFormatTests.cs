namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The golden format itself. Every corpus assertion is an equality on this text, so a format that
/// dropped a distinction would make the whole suite green and meaningless. These tests pin the
/// distinctions the format promises to keep, and the properties that make it readable in a diff.
/// </summary>
public class GridSnapshotFormatTests
{
    [Fact]
    public void Text_ShowsWhatIsOnTheScreen_RightTrimmed()
    {
        var grid = StubGrid.Of(4, 2);
        grid.Write(0, 0, "hi");

        Assert.Contains("  r00 |hi|", Render(grid));
    }

    [Fact]
    public void Text_CollapsesARunOfUntouchedRows()
    {
        var grid = StubGrid.Of(4, 6);
        grid.Write(0, 0, "top");

        var lines = Render(grid);

        Assert.Contains("  r01-r05 ~blank~", lines);
    }

    [Fact]
    public void Text_KeepsASpaceThatCarriesABackgroundColour()
    {
        var grid = StubGrid.Of(4, 1);
        grid.Paint(0, 0, 2, new CellStyle(TerminalColor.Default, TerminalColor.Indexed(4), CellAttributes.None));

        var lines = Render(grid);

        Assert.Contains("  r00 |  |", lines);
        Assert.Contains("  s00 |aa|", lines);
    }

    [Fact]
    public void Style_AlignsColumnForColumnWithTheText()
    {
        var grid = StubGrid.Of(6, 1);
        grid.Write(0, 0, "abcdef");
        grid.Paint(2, 0, 2, new CellStyle(TerminalColor.Rgb(255, 0, 0), TerminalColor.Default, CellAttributes.None));

        var lines = Render(grid);
        var text = lines.Single(line => line.StartsWith("  r00 ", StringComparison.Ordinal));
        var style = lines.Single(line => line.StartsWith("  s00 ", StringComparison.Ordinal));

        Assert.Equal("  r00 |abcdef|", text);
        Assert.Equal("  s00 |..aa..|", style);
        Assert.Equal(text.Length, style.Length);
    }

    [Fact]
    public void Legend_SpellsOutEveryStyleTheFrameUsed()
    {
        var grid = StubGrid.Of(4, 1);
        grid.Paint(0, 0, 1, new CellStyle(TerminalColor.Rgb(0xff, 0x87, 0x00), TerminalColor.Default, CellAttributes.Bold));

        Assert.Contains("  a  #ff8700 on default bold", Render(grid));
    }

    [Fact]
    public void Legend_OrdersStylesByColour_SoAddingOneDoesNotRenumberTheRest()
    {
        var cool = new CellStyle(TerminalColor.Indexed(2), TerminalColor.Default, CellAttributes.None);
        var warm = new CellStyle(TerminalColor.Indexed(9), TerminalColor.Default, CellAttributes.None);

        var painted = StubGrid.Of(4, 1);
        painted.Paint(0, 0, 1, warm);
        painted.Paint(1, 0, 1, cool);

        var reversed = StubGrid.Of(4, 1);
        reversed.Paint(0, 0, 1, cool);
        reversed.Paint(1, 0, 1, warm);

        Assert.Contains("  s00 |ba|", Render(painted));
        Assert.Contains("  s00 |ab|", Render(reversed));
    }

    [Fact]
    public void Width_PlaneAppearsOnlyWhenAFrameHoldsAWideCharacter()
    {
        var narrow = StubGrid.Of(4, 1);
        narrow.Write(0, 0, "ab");

        Assert.DoesNotContain("width", Render(narrow));
    }

    [Fact]
    public void Width_MarksTheLeadCellAndTheTrailerOfAWideCharacter()
    {
        var grid = StubGrid.Of(4, 1);
        grid.WriteWide(0, 0, new Rune('漢'));

        var lines = Render(grid);

        Assert.Contains("width", lines);
        Assert.Contains("  w00 |W-|", lines);

        Assert.Contains("  r00 |漢 |", lines);
    }

    [Fact]
    public void Runes_ListsACellWhoseRuneHasNoGlyph_SoTheTextPlaneStaysAligned()
    {
        var grid = StubGrid.Of(4, 1);
        grid.Write(0, 0, "a");
        grid.Put(1, 0, new Rune(0x0001));

        var lines = Render(grid);

        Assert.Contains("  r00 |a¤|", lines);
        Assert.Contains("  r00c001 U+0001", lines);
    }

    [Fact]
    public void Header_CarriesTheStateAGoldenHasToPinBesidesTheCells()
    {
        var state = new TerminalState(
            new TerminalCursor(12, 3, Visible: false, Shape: CursorShape.Bar, Blinking: false),
            Reset.Modes with
            {
                AlternateScreen = true,
                BracketedPaste = true,
                MouseTracking = MouseTracking.AnyEvent,
                MouseEncoding = MouseEncoding.Sgr,
            },
            Title: "claude",
            IconTitle: "");

        var lines = GridSnapshot.Capture(StubGrid.Of(4, 1), state, "label").ToLines().ToList();

        Assert.Contains("alt on", lines);
        Assert.Contains("cursor col=12 row=3 hidden shape=bar steady", lines);
        Assert.Contains("title \"claude\"", lines);
        Assert.Contains("modes autowrap bracketed-paste mouse=anyevent/sgr kitty=none", lines);
    }

    [Fact]
    public void Header_ReportsTheKeyboardNegotiationTheInputEncoderDependsOn()
    {
        var state = Reset.State with
        {
            Modes = Reset.Modes with { KeyboardProtocolFlags = 5, ModifyOtherKeys = 2 },
        };

        var lines = GridSnapshot.Capture(StubGrid.Of(4, 1), state, "label").ToLines().ToList();

        Assert.Contains("modes autowrap mouse=off/x10 kitty=5 modify-other-keys=2", lines);
    }

    [Fact]
    public void Header_EscapesATitleThatWouldBreakTheLine()
    {
        var state = Reset.State with { Title = "a\"b\nc" };

        var lines = GridSnapshot.Capture(StubGrid.Of(4, 1), state, "label").ToLines().ToList();

        Assert.Contains("title \"a\\\"b\\u000Ac\"", lines);
    }

    static List<string> Render(StubGrid grid) =>
        GridSnapshot.Capture(grid, Reset.State, "test").ToLines().ToList();
}
