using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// The two coordinates a diff counts in: <see cref="FileLine"/>, a line of one side's file, and
/// <see cref="RowIndex"/>, a slot in the flattened row stream. The stream carries banners, hunk
/// bars and tears the file has no lines for, and drops the lines a fold or an unexpanded gap
/// hides, so neither direction of the mapping is total — and the row set is the only thing that
/// knows either one.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class DiffRowCoordinateTests(CodeIntelFixture fixture)
{
    // A rename banner, a plain bar above the first hunk, a removal paired with an addition, a
    // 56-line gap wide enough to split into bar/tear/bar, then a second hunk. Every row kind the
    // stream has, in one file.
    //
    //   0 banner   1 bar   2 ctx 1/1   3 del 2/-   4 add -/2   5 ctx 3/3
    //   6 bar      7 tear  8 bar       9 ctx 60/60  10 ctx 61/61
    private static DiffRowSet Renamed() => DiffRowSet.Build(
        new DiffRenderState.Loaded(
            DiffOf(
                "src/Runner.cs",
                Hunk(1, 3, 1, 3, null,
                    new DiffLine(DiffLineKind.Context, 1, 1, "one"),
                    new DiffLine(DiffLineKind.Removed, 2, null, "two-before"),
                    new DiffLine(DiffLineKind.Added, null, 2, "two-after"),
                    new DiffLine(DiffLineKind.Context, 3, 3, "three")),
                Hunk(60, 2, 60, 2, null,
                    new DiffLine(DiffLineKind.Context, 60, 60, "sixty"),
                    new DiffLine(DiffLineKind.Context, 61, 61, "sixty-one")))),
        Loc());

    // ---- the stream's shape, so the row numbers below mean something ----

    [Fact]
    public void TheFixtureCarriesEveryRowKind()
    {
        var rows = Renamed().Rows;

        Assert.IsType<DiffRow.Banner>(rows[0]);
        Assert.IsType<DiffRow.HunkSeparator>(rows[1]);
        Assert.IsType<DiffRow.Tear>(rows[7]);
        Assert.Equal(11, rows.Count);
    }

    // ---- rows to lines ----

    [Fact]
    public void ChromeRowsStandForNoFileLineOnEitherSide()
    {
        var set = Renamed();

        foreach (var row in new[] { 0, 1, 6, 7, 8 })
        {
            Assert.Null(set.NewLineAt(new RowIndex(row)));
            Assert.Null(set.OldLineAt(new RowIndex(row)));
        }
    }

    // Positions outlive the row set they were captured against, so the mapping answers for a row
    // that isn't there rather than throwing.
    [Fact]
    public void ARowOutsideTheStreamStandsForNoFileLine()
    {
        var set = Renamed();

        Assert.Null(set.NewLineAt(new RowIndex(-1)));
        Assert.Null(set.NewLineAt(new RowIndex(999)));
        Assert.Null(set.OldLineAt(new RowIndex(999)));
    }

    [Fact]
    public void ARemovedLineHasNoAfterSideNumberAndAnAddedLineNoBeforeSideOne()
    {
        var set = Renamed();

        Assert.Equal(new FileLine(2), set.OldLineAt(new RowIndex(3)));
        Assert.Null(set.NewLineAt(new RowIndex(3)));

        Assert.Equal(new FileLine(2), set.NewLineAt(new RowIndex(4)));
        Assert.Null(set.OldLineAt(new RowIndex(4)));
    }

    // ---- lines to rows ----

    [Fact]
    public void EveryNumberedRowSurvivesTheRoundTripBackToItself()
    {
        var set = Renamed();

        for (var i = 0; i < set.Rows.Count; i++)
        {
            var row = new RowIndex(i);
            if (set.NewLineAt(row) is not { } line) continue;
            Assert.Equal(row, set.RowForNewLine(line));
        }
    }

    [Fact]
    public void ALineInsideAnUnexpandedGapHasNoRow()
    {
        var set = Renamed();

        Assert.Null(set.RowForNewLine(new FileLine(30)));
        Assert.Null(set.RowForNewLine(new FileLine(4)));
    }

    [Fact]
    public void ALineBeyondTheEndOfTheStreamHasNoRow()
    {
        Assert.Null(Renamed().RowForNewLine(new FileLine(4000)));
    }

    // The removed line exists only in the before-side file, so nothing on the after-side axis can
    // reach its row — after-side line 2 is the addition that replaced it.
    [Fact]
    public void ALineOnlyOneSideHasIsUnreachableFromTheOtherSidesAxis()
    {
        var set = Renamed();

        Assert.Equal(new RowIndex(4), set.RowForNewLine(new FileLine(2)));
        Assert.Equal(new FileLine(2), set.OldLineAt(new RowIndex(3)));
        Assert.Null(set.NewLineAt(new RowIndex(3)));
    }

    // ---- where a scroll lands when the exact row isn't there ----

    [Fact]
    public void AScrollTargetInsideAGapLandsOnTheClosestRowAboveIt()
    {
        var set = Renamed();

        Assert.Equal(new RowIndex(5), set.RowNearestNewLine(new FileLine(30)));
        Assert.Equal(new RowIndex(9), set.RowNearestNewLine(new FileLine(60)));
    }

    [Fact]
    public void AScrollTargetAboveEveryNumberedRowLandsNowhere()
    {
        var set = DiffRowSet.Build(
            new DiffRenderState.Loaded(
                DiffOf(
                    null,
                    Hunk(40, 1, 40, 1, null, new DiffLine(DiffLineKind.Context, 40, 40, "forty")))),
            Loc());

        Assert.Null(set.RowNearestNewLine(new FileLine(1)));
    }

    // ---- folds ----

    [Fact]
    public void ALineACollapsedFoldSwallowedHasNoRowButScrollsToTheFoldItself()
    {
        var open = FullFile(FoldState.Open(FoldPath));
        var methodId = open.Rows.OfType<DiffRow.Line>().Last(r => r.Fold is { Chevron: true }).Fold!.Value.Id;
        var body = new FileLine(FoldedBodyLine);
        Assert.NotNull(open.RowForNewLine(body));

        var collapsed = FullFile(FoldState.Open(FoldPath).Toggled(methodId));

        Assert.Null(collapsed.RowForNewLine(body));
        var chip = collapsed.RowNearestNewLine(body);
        Assert.NotNull(chip);
        Assert.True(collapsed.Rows[chip.Value.Value] is DiffRow.Line { Fold.Chip: true });
    }

    [Fact]
    public void AWholeFileMapsEveryLineToItsOwnRowAndBack()
    {
        var set = FullFile(null);

        for (var i = 0; i < set.Rows.Count; i++)
        {
            var row = new RowIndex(i);
            var line = set.NewLineAt(row);
            Assert.NotNull(line);
            Assert.Equal(new FileLine(i + 1), line);
            Assert.Equal(row, set.RowForNewLine(line.Value));
            // A whole-file render draws one after-side gutter; nothing has a before-side line.
            Assert.Null(set.OldLineAt(row));
        }
    }

    // ---- gutter sizing still comes off the digits ----

    [Fact]
    public void GutterDigitsCoverTheWidestNumberOnEitherSide()
    {
        Assert.Equal(2, Renamed().GutterDigits);

        var wide = DiffRowSet.Build(
            new DiffRenderState.Loaded(
                DiffOf(
                    null,
                    Hunk(1234, 1, 1234, 1, null, new DiffLine(DiffLineKind.Context, 1234, 1234, "deep")))),
            Loc());

        Assert.Equal(4, wide.GutterDigits);
    }

    [Fact]
    public void AFourDigitWholeFileSizesItsGutterForFourDigits()
    {
        var lines = Enumerable.Range(1, 1200).Select(i => "// " + i).ToArray();
        var set = DiffRowSet.Build(
            new DiffRenderState.FullFile(
                "src/long.cs", lines, new HashSet<int>(), DiffSide.WorkingTree, false),
            Loc());

        Assert.Equal(4, set.GutterDigits);
    }

    // ---- the cell itself ----

    [Fact]
    public void AGutterCellCarriesTheLineAndTheDigitsThatDrawIt()
    {
        var cell = DiffGutterNumber.Of(new FileLine(1234));

        Assert.Equal(new FileLine(1234), cell.Line);
        Assert.Equal("1234", cell.Text);
    }

    // One representation of "no number on this side", so a row built without one and the default
    // value are the same thing rather than two shapes of empty.
    [Fact]
    public void AMissingNumberIsTheDefaultValueAndDrawsNothing()
    {
        Assert.Equal(DiffGutterNumber.None, default);
        Assert.Equal(DiffGutterNumber.None, DiffGutterNumber.Of(null));
        Assert.Null(DiffGutterNumber.None.Line);
        Assert.Equal(string.Empty, DiffGutterNumber.None.Text);
    }

    // ---- helpers ----

    private const string FoldPath = "src/AuthService.cs";

    // Line 5 sits inside Login's body, which the fold collapses.
    private const int FoldedBodyLine = 5;

    private static readonly string[] FoldSource =
    [
        "class AuthService",
        "{",
        "    void Login(string user)",
        "    {",
        "        Check(user);",
        "        Issue(user);",
        "    }",
        "}",
    ];

    private DiffRowSet FullFile(FoldState? folds) => DiffRowSet.Build(
        new DiffRenderState.FullFile(
            FoldPath,
            FoldSource,
            AddedLineNumbers: new HashSet<int>(),
            Side: DiffSide.WorkingTree,
            Truncated: false,
            Emphasis: null,
            Annotations: new DiffAnnotations(null, fixture.Outline(string.Join('\n', FoldSource)), null)),
        Loc(),
        folds);

    private static ILocalizationService Loc() => new LocalizationService(new State<Locale>(Locale.En));

    private static DiffResult DiffOf(string? oldPath, params DiffHunk[] hunks) => new(
        RepoId: Guid.Empty,
        Path: "src/Runner.cs",
        OldPath: oldPath,
        Side: DiffSide.Unstaged,
        IsBinary: false,
        IsModeOnly: false,
        OldMode: null,
        NewMode: null,
        Hunks: hunks,
        Truncated: false,
        ErrorMessage: null);

    private static DiffHunk Hunk(
        int oldStart, int oldLines, int newStart, int newLines, string? header, params DiffLine[] lines)
        => new(oldStart, oldLines, newStart, newLines, header, lines);
}
