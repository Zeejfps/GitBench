using System.Text;
using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The real styler driving the real splitter. Each was built against a stand-in for the other — the
/// splitter's tests use a hand-written styler and the styler's tests never split a row — so the
/// properties that only exist where the two meet have no other home.
/// </summary>
public class TerminalRowCompositionTests
{
    private static readonly ICellStyler Styler = new TerminalCellStyler(ThemeStyles.Dark.Terminal);

    private static TerminalCell Cell(char text, CellWidth width = CellWidth.Single,
        CellAttributes attributes = CellAttributes.None) =>
        new(new Rune(text), TerminalColor.Default, TerminalColor.Default, attributes, width);

    private static TerminalRowRuns Split(params TerminalCell[] row) =>
        TerminalRowRuns.Split(row, Styler, new int[row.Length], new TerminalRowRun[row.Length]);

    [Fact]
    public void AWideCharacter_StaysInOneRunWithTheTextAroundIt()
    {
        // The property that decides whether CJK draws at all. The trailer carries no rune and no
        // colour of its own, so the styler has to answer for it exactly as it does for the leader —
        // if it did not, every wide glyph would end its run and a CJK line would be one draw call
        // per character.
        var runs = Split(
            Cell('a'),
            Cell('一', CellWidth.WideLeader),
            Cell(' ', CellWidth.WideTrailer),
            Cell('b'));

        Assert.Equal(1, runs.Runs.Length);
        var run = runs.Runs[0];
        Assert.Equal(0, run.Column);
        Assert.Equal(4, run.Length);
        Assert.Equal(['a', 0x4E00, ' ', 'b'], runs.CodePointsOf(run).ToArray());
    }

    [Fact]
    public void AStyleChange_SplitsTheRowAndEachRunKeepsItsOwnColours()
    {
        var runs = Split(
            Cell('a'),
            Cell('b', attributes: CellAttributes.Bold),
            Cell('c', attributes: CellAttributes.Bold));

        Assert.Equal(2, runs.Runs.Length);
        Assert.False(runs.Runs[0].Style.Bold);
        Assert.True(runs.Runs[1].Style.Bold);
        Assert.Equal(ThemeStyles.Dark.Terminal.DefaultForeground, runs.Runs[0].Style.Foreground);
    }

    [Fact]
    public void AnInverseCell_SplitsFromItsNeighboursAndSwapsTheThemesPair()
    {
        var theme = ThemeStyles.Dark.Terminal;

        var runs = Split(Cell('a'), Cell('b', attributes: CellAttributes.Inverse));

        Assert.Equal(2, runs.Runs.Length);
        Assert.Equal(theme.DefaultBackground, runs.Runs[1].Style.Foreground);
        Assert.Equal(theme.DefaultForeground, runs.Runs[1].Style.Background);
    }

    [Fact]
    public void CellsDifferingOnlyByAttributesThatCarryNoColourOrForm_ShareARun()
    {
        // Blink is parsed by the engine and drawn by nothing. If it reached RunStyle it would split
        // runs for a difference no pixel expresses.
        var runs = Split(Cell('a'), Cell('b', attributes: CellAttributes.Blink));

        Assert.Equal(1, runs.Runs.Length);
    }

    [Fact]
    public void EveryRunOfARealisticRow_CanBeDrawn()
    {
        var row = new[]
        {
            Cell('$'),
            Cell(' '),
            Cell('g', attributes: CellAttributes.Bold),
            Cell('i', attributes: CellAttributes.Bold),
            Cell('t', attributes: CellAttributes.Bold),
            Cell(' '),
            Cell('l', attributes: CellAttributes.Underline),
            Cell('o', attributes: CellAttributes.Underline),
            Cell('g', attributes: CellAttributes.Underline),
        };

        var runs = Split(row);

        // Contiguous, ascending, covering the row exactly once — what a renderer walks.
        var column = 0;
        foreach (var run in runs.Runs)
        {
            Assert.Equal(column, run.Column);
            Assert.Equal(run.Length, runs.CodePointsOf(run).Length);
            column += run.Length;
        }

        Assert.Equal(row.Length, column);

        // plain "$ ", bold "git", plain " ", underlined "log" — the blank between the two words
        // belongs to neither and is its own run.
        Assert.Equal(4, runs.Runs.Length);
    }
}
