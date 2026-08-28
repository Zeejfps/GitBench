using System.Runtime.CompilerServices;
using System.Text;
using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The contract of <see cref="TerminalRowRuns"/>: one row of cells in, the draw runs and the one
/// code point per column out. These pin the nine acceptance criteria of module 5 of
/// <c>docs/plans/terminal.md</c> — that runs are maximal and total, that style comes from the
/// styler and nowhere else, that a wide trailer draws a space rather than a second glyph, and that
/// splitting a row costs no allocation and never half-fills a buffer it cannot fit.
/// </summary>
public class TerminalRowRunsTests
{
    private const int Untouched = -1;

    private static readonly ColorStyler Styler = new();

    // AC4 — Runs are maximal and total: ascending, contiguous, covering every column exactly once,
    // lengths summing to N; no run spans a style change; no two consecutive runs have equal styles.
    // Every successful split below also goes through AssertTotalAndMaximal, which pins that
    // generically; the per-test assertions still state the concrete shape expected.

    [Fact]
    public void Split_UniformRow_YieldsOneRunSpanningEveryColumn()
    {
        var row = new[] { Cell('a'), Cell('b'), Cell('c'), Cell('d') };
        var codePoints = Buffer(4);
        var runs = new TerminalRowRun[4];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(new[] { new TerminalRowRun(0, 4, Styler.Style(row[0])) }, split.Runs.ToArray());
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_StyleChangesMidRow_BreaksARunAtEachChange()
    {
        var row = new[] { Cell('a', fg: 1), Cell('b', fg: 1), Cell('c', fg: 2), Cell('d', fg: 2), Cell('e', fg: 1) };
        var codePoints = Buffer(5);
        var runs = new TerminalRowRun[5];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(
            new[]
            {
                new TerminalRowRun(0, 2, Styler.Style(row[0])),
                new TerminalRowRun(2, 2, Styler.Style(row[2])),
                new TerminalRowRun(4, 1, Styler.Style(row[4])),
            },
            split.Runs.ToArray());
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_StyleChangingEveryColumn_YieldsOneRunPerColumn()
    {
        var row = new[] { Cell('a', fg: 1), Cell('a', fg: 2), Cell('a', fg: 3), Cell('a', fg: 4) };
        var codePoints = Buffer(4);
        var runs = new TerminalRowRun[4];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(
            new[]
            {
                new TerminalRowRun(0, 1, Styler.Style(row[0])),
                new TerminalRowRun(1, 1, Styler.Style(row[1])),
                new TerminalRowRun(2, 1, Styler.Style(row[2])),
                new TerminalRowRun(3, 1, Styler.Style(row[3])),
            },
            split.Runs.ToArray());
        AssertTotalAndMaximal(row.Length, split);
    }

    // AC5 — Style comes only from `ICellStyler` — cells with differing runes but an equal `RunStyle`
    // merge into one run; cells with identical runes but differing `RunStyle` split into two. There
    // is no wide-cell run-merging special case in either direction: a trailer whose style differs
    // from its leader starts a new run, and a wide pair styled like its neighbours does not break
    // the run it sits in.

    [Fact]
    public void Split_DifferingCellsWithOneStyle_MergeIntoASingleRun()
    {
        var row = new[] { Cell('a', fg: 1), Cell('\u4e2d', fg: 2), Cell('c', fg: 3, attributes: CellAttributes.Bold) };
        var codePoints = Buffer(3);
        var runs = new TerminalRowRun[3];

        var split = TerminalRowRuns.Split(row, ConstantStyler.Instance, codePoints, runs);

        Assert.Equal(new[] { new TerminalRowRun(0, 3, ConstantStyler.OnlyStyle) }, split.Runs.ToArray());
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_IdenticalRunesWithDifferingStyles_SplitIntoSeparateRuns()
    {
        var row = new[] { Cell('a', fg: 1), Cell('a', fg: 2) };
        var codePoints = Buffer(2);
        var runs = new TerminalRowRun[2];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(
            new[]
            {
                new TerminalRowRun(0, 1, Styler.Style(row[0])),
                new TerminalRowRun(1, 1, Styler.Style(row[1])),
            },
            split.Runs.ToArray());
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_WideTrailerStyledUnlikeItsLeader_StartsANewRun()
    {
        var row = new[]
        {
            Cell('\u4e2d', fg: 1, width: CellWidth.WideLeader),
            Cell('\u4e2d', fg: 2, width: CellWidth.WideTrailer),
        };
        var codePoints = Buffer(2);
        var runs = new TerminalRowRun[2];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(
            new[]
            {
                new TerminalRowRun(0, 1, Styler.Style(row[0])),
                new TerminalRowRun(1, 1, Styler.Style(row[1])),
            },
            split.Runs.ToArray());
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_WidePairInsideAUniformRow_StaysInsideOneRun()
    {
        var row = new[]
        {
            Cell('a'),
            Cell('\u4e2d', width: CellWidth.WideLeader),
            Cell('\u4e2d', width: CellWidth.WideTrailer),
            Cell('b'),
        };
        var codePoints = Buffer(4);
        var runs = new TerminalRowRun[4];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(new[] { new TerminalRowRun(0, 4, Styler.Style(row[0])) }, split.Runs.ToArray());
        Assert.Equal(new[] { 0x61, 0x4e2d, 0x20, 0x62 }, codePoints);
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_StyleChangeAfterAWidePair_BreaksAtTheTrailersColumnPlusOne()
    {
        var row = new[]
        {
            Cell('\u4e2d', fg: 1, width: CellWidth.WideLeader),
            Cell('\u4e2d', fg: 1, width: CellWidth.WideTrailer),
            Cell('z', fg: 2),
        };
        var codePoints = Buffer(3);
        var runs = new TerminalRowRun[3];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(
            new[]
            {
                new TerminalRowRun(0, 2, Styler.Style(row[0])),
                new TerminalRowRun(2, 1, Styler.Style(row[2])),
            },
            split.Runs.ToArray());
        AssertTotalAndMaximal(row.Length, split);
    }

    // AC1 — One code point per column: for a row of N cells, indices 0..N-1 of the caller's
    // code-point buffer are filled, the tail of a longer buffer is left untouched, and each run's
    // text is exactly `codePoints.Slice(run.Column, run.Length)`.
    // AC2 — `Single` / `WideLeader` columns carry the cell's `Rune.Value` verbatim — no
    // substitution, no normalization.
    // AC3 — A `WideTrailer` column carries U+0020, regardless of what rune that cell holds.
    // AC7 — A cell with non-null `Combining` contributes exactly one code point, its base `Rune`.

    [Fact]
    public void Split_SingleWidthCells_CarryTheirRuneValuesVerbatim()
    {
        var row = new[] { Cell('A'), Cell('\u00e9'), Cell('\u2500') };
        var codePoints = Buffer(3);
        var runs = new TerminalRowRun[3];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(new[] { 0x41, 0x00e9, 0x2500 }, codePoints);
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_WideLeader_CarriesItsWholeRuneEvenAboveTheBmp()
    {
        var grinning = new Rune(0x1f600);
        var row = new[]
        {
            Cell(grinning, width: CellWidth.WideLeader),
            Cell(grinning, width: CellWidth.WideTrailer),
        };
        var codePoints = Buffer(2);
        var runs = new TerminalRowRun[2];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(new[] { 0x1f600, 0x20 }, codePoints);
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_WideTrailerHoldingAnyRune_CarriesASpace()
    {
        var row = new[] { Cell('\u4e2d', width: CellWidth.WideLeader), Cell('X', width: CellWidth.WideTrailer) };
        var codePoints = Buffer(2);
        var runs = new TerminalRowRun[2];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(new[] { 0x4e2d, 0x20 }, codePoints);
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_CellWithCombiningMarks_ContributesOnlyItsBaseRune()
    {
        var row = new[] { Cell('x'), Cell('e') with { Combining = "\u0301" }, Cell('y') };
        var codePoints = Buffer(3);
        var runs = new TerminalRowRun[3];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(new[] { 0x78, 0x65, 0x79 }, codePoints);
        Assert.Equal(new[] { new TerminalRowRun(0, 3, Styler.Style(row[0])) }, split.Runs.ToArray());
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_RunCodePoints_AreTheCallersBufferAtTheRunsColumns()
    {
        var row = new[] { Cell('a', fg: 1), Cell('b', fg: 1), Cell('c', fg: 2), Cell('d', fg: 2) };
        var codePoints = Buffer(4);
        var runs = new TerminalRowRun[4];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);
        var second = split.Runs[1];

        Assert.Equal(new[] { 0x63, 0x64 }, split.CodePointsOf(second).ToArray());
        Assert.Equal(codePoints.AsSpan(second.Column, second.Length).ToArray(), split.CodePointsOf(second).ToArray());
        AssertTotalAndMaximal(row.Length, split);
    }

    // AC6 — A zero-length row yields zero runs and no writes. Plus the cardinality and
    // longer-buffer boundaries either side of it.

    [Fact]
    public void Split_EmptyRow_YieldsNoRunsAndWritesNothing()
    {
        var codePoints = Buffer(4);
        var runs = new TerminalRowRun[4];

        var split = TerminalRowRuns.Split(ReadOnlySpan<TerminalCell>.Empty, Styler, codePoints, runs);

        Assert.Empty(split.Runs.ToArray());
        Assert.Equal(new[] { Untouched, Untouched, Untouched, Untouched }, codePoints);
        AssertTotalAndMaximal(0, split);
    }

    [Fact]
    public void Split_SingleCellRow_YieldsOneRunOfOneColumn()
    {
        var row = new[] { Cell('a') };
        var codePoints = Buffer(1);
        var runs = new TerminalRowRun[1];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(new[] { new TerminalRowRun(0, 1, Styler.Style(row[0])) }, split.Runs.ToArray());
        Assert.Equal(new[] { 0x61 }, codePoints);
        AssertTotalAndMaximal(row.Length, split);
    }

    [Fact]
    public void Split_BuffersLongerThanTheRow_FillAndExposeOnlyTheRowsColumns()
    {
        var row = new[] { Cell('a'), Cell('b') };
        var codePoints = Buffer(6);
        var runs = new TerminalRowRun[6];

        var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);

        Assert.Equal(new[] { new TerminalRowRun(0, 2, Styler.Style(row[0])) }, split.Runs.ToArray());
        Assert.Equal(new[] { 0x61, 0x62, Untouched, Untouched, Untouched, Untouched }, codePoints);
        AssertTotalAndMaximal(row.Length, split);
    }

    // AC9 — An output buffer shorter than the row is rejected loudly, not silently truncated. Both
    // buffers have the same required length, so `ParamName` is the only thing that tells the two
    // failures apart.

    [Fact]
    public void Split_CodePointBufferShorterThanTheRow_ThrowsBeforeWritingAnything()
    {
        var row = new[] { Cell('a'), Cell('b'), Cell('c') };
        var codePoints = Buffer(2);
        var runs = new TerminalRowRun[3];

        var thrown = Assert.Throws<ArgumentException>(
            () => { TerminalRowRuns.Split(row, Styler, codePoints, runs); });

        Assert.Equal("codePoints", thrown.ParamName);
        Assert.Equal(new[] { Untouched, Untouched }, codePoints);
    }

    [Fact]
    public void Split_RunBufferShorterThanTheRow_ThrowsBeforeWritingAnything()
    {
        // Uniform, so this row would in fact fit one run — the buffer must be big enough for the
        // worst case, and the check must not depend on the data.
        var row = new[] { Cell('a'), Cell('a'), Cell('a'), Cell('a') };
        var codePoints = Buffer(4);
        var runs = new TerminalRowRun[3];

        var thrown = Assert.Throws<ArgumentException>(
            () => { TerminalRowRuns.Split(row, Styler, codePoints, runs); });

        Assert.Equal("runs", thrown.ParamName);
        Assert.Equal(new[] { Untouched, Untouched, Untouched, Untouched }, codePoints);
    }

    // AC8 — No managed allocation per call — the caller owns both output buffers.

    [Fact]
    public void Split_RepeatedCalls_AllocateNothing()
    {
        // This one cannot flake, and should not be quarantined as if it could: a correct
        // implementation has structurally nothing to allocate, GC.GetAllocatedBytesForCurrentThread
        // is an exact per-thread counter rather than a sample, and the measured body is fully
        // synchronous on this thread. It fails if and only if AC8 is violated. The measured loop
        // lives in a NoInlining helper that is run once first, so JIT work is not counted.
        var row = new TerminalCell[80];
        FillAlternating(row);
        var codePoints = new int[80];
        var runs = new TerminalRowRun[80];

        SplitRepeatedly(row, codePoints, runs, iterations: 500);

        var before = GC.GetAllocatedBytesForCurrentThread();
        SplitRepeatedly(row, codePoints, runs, iterations: 500);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SplitRepeatedly(
        ReadOnlySpan<TerminalCell> row,
        Span<int> codePoints,
        Span<TerminalRowRun> runs,
        int iterations)
    {
        var seen = 0;
        for (var i = 0; i < iterations; i++)
        {
            var split = TerminalRowRuns.Split(row, Styler, codePoints, runs);
            seen += split.Runs.Length;
        }

        return seen;
    }

    /// <summary>
    /// AC4 as an invariant rather than as examples: the runs tile the row's columns in order,
    /// exactly once each, and no two neighbours share a style.
    /// </summary>
    private static void AssertTotalAndMaximal(int columns, in TerminalRowRuns split)
    {
        var column = 0;
        for (var i = 0; i < split.Runs.Length; i++)
        {
            var run = split.Runs[i];
            Assert.Equal(column, run.Column);
            Assert.True(run.Length > 0);
            if (i > 0) Assert.NotEqual(split.Runs[i - 1].Style, run.Style);
            column += run.Length;
        }

        Assert.Equal(columns, column);
    }

    private static void FillAlternating(Span<TerminalCell> row)
    {
        for (var i = 0; i < row.Length; i++)
            row[i] = Cell((char)('a' + (i % 26)), fg: (byte)(1 + (i / 4) % 3));
    }

    private static int[] Buffer(int length)
    {
        var buffer = new int[length];
        buffer.AsSpan().Fill(Untouched);
        return buffer;
    }

    private static TerminalCell Cell(
        char rune,
        byte fg = 7,
        CellWidth width = CellWidth.Single,
        CellAttributes attributes = CellAttributes.None)
        => Cell(new Rune(rune), fg, width, attributes);

    private static TerminalCell Cell(
        Rune rune,
        byte fg = 7,
        CellWidth width = CellWidth.Single,
        CellAttributes attributes = CellAttributes.None)
        => new(rune, TerminalColor.Indexed(fg), TerminalColor.Default, attributes, width);

    /// <summary>
    /// A styler with nothing hidden in it: the cell's own colours and attributes, so a test makes
    /// two cells look alike or unlike by choosing their colours rather than by standing up a theme.
    /// </summary>
    private sealed class ColorStyler : ICellStyler
    {
        public RunStyle Style(in TerminalCell cell) => new(
            Pack(cell.Foreground),
            Pack(cell.Background),
            cell.Has(CellAttributes.Bold),
            cell.Has(CellAttributes.Italic),
            cell.Has(CellAttributes.Underline),
            cell.Has(CellAttributes.CrossedOut));

        private static uint Pack(TerminalColor color) => ((uint)color.Kind << 8) | color.Index;
    }

    /// <summary>One style for every cell, however different the cells are.</summary>
    private sealed class ConstantStyler : ICellStyler
    {
        public static readonly ConstantStyler Instance = new();

        public static readonly RunStyle OnlyStyle = new(0xff112233, 0xff000000, false, false, false, false);

        public RunStyle Style(in TerminalCell cell) => OnlyStyle;
    }
}
