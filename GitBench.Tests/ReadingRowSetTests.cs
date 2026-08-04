using GitBench.Features.Diff;
using GitBench.Features.Diff.Reading;
using GitBench.Git;
using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

// Reading mode is a second way to draw the same diff, so the flattener has to honour a plan
// without the DiffResult under it changing at all — that is what keeps staging, discarding and
// hunk selection acting on the real change while the reader looks at the abridged one.
public class ReadingRowSetTests
{
    private static readonly ILocalizationService Loc = new LocalizationService(new State<Locale>(Locale.En));

    private static DiffResult File(string path, params DiffHunk[] hunks)
        => new(
            RepoId: Guid.Empty,
            Path: path,
            OldPath: null,
            Side: DiffSide.Commit,
            IsBinary: false,
            IsModeOnly: false,
            OldMode: null,
            NewMode: null,
            Hunks: hunks,
            Truncated: false,
            ErrorMessage: null);

    private static DiffHunk Hunk(params DiffLine[] lines) => new(1, lines.Length, 1, lines.Length, null, lines);

    private static DiffLine Add(int n, string text) => new(DiffLineKind.Added, null, n, text);
    private static DiffLine Ctx(int n, string text) => new(DiffLineKind.Context, n, n, text);

    private static DiffRowSet Flatten(DiffResult file, ReadingOverlay? overlay, params int[] expandedFolds)
    {
        var reading = overlay is null
            ? null
            : new ReadingView(overlay, expandedFolds.Length == 0 ? null : new HashSet<int>(expandedFolds));
        return DiffRowSet.Build(new DiffRenderState.Loaded(file, null, null, reading), Loc);
    }

    private static IReadOnlyList<string> LineTexts(DiffRowSet set) =>
        set.Rows.OfType<DiffRow.Line>().Select(l => l.Text).ToList();

    [Fact]
    public void DrawsTheWholeDiffWhenNoPlanIsApplied()
    {
        var file = File("a.txt", Hunk(Add(1, "one"), Add(2, "two"), Add(3, "three")));

        Assert.Equal(["one", "two", "three"], LineTexts(Flatten(file, null)));
    }

    [Fact]
    public void OmitsRemovedRowsAndKeepsTheRest()
    {
        var file = File("a.txt", Hunk(Add(1, "one"), Add(2, "two"), Add(3, "three")));
        var index = ReadingRowIndex.Build([file]);
        var overlay = ReadingPlanCompiler.Compile(index, new ReadingPlan([new ReadingRemoval(2, 2)], [], [])).Overlay!;

        Assert.Equal(["one", "three"], LineTexts(Flatten(file, overlay)));
    }

    [Fact]
    public void DrawsAFoldedRunAsOneEllipsisRowCarryingItsCount()
    {
        var file = File("a.txt", Hunk(
            Add(1, "func f() {"),
            Add(2, "    a := 1"),
            Add(3, "    b := 2"),
            Add(4, "}")));
        var index = ReadingRowIndex.Build([file]);
        var overlay = ReadingPlanCompiler.Compile(index, new ReadingPlan([], [], [new ReadingFold(2, 3)])).Overlay!;

        var set = Flatten(file, overlay);
        var fold = Assert.Single(set.Rows.OfType<DiffRow.Fold>());

        Assert.Equal("    …", fold.Text);
        Assert.Equal(2, fold.HiddenCount);
        Assert.Equal(DiffLineKind.Added, fold.Kind);
        Assert.Equal(["func f() {", "}"], LineTexts(set));
    }

    // A plan routinely folds one run in several pieces. Drawn literally that is a stack of "… N
    // hidden lines" rows saying nothing a single row would not, so they merge — and the merged row
    // has to name every piece, or clicking it hands back less than it claimed was there.
    [Fact]
    public void DrawsAdjacentFoldsAsOneRowThatReopensAllOfThem()
    {
        var file = File("a.txt", Hunk(
            Add(1, "keep"),
            Add(2, "a1"), Add(3, "a2"),
            Add(4, "b1"), Add(5, "b2"), Add(6, "b3"),
            Add(7, "tail")));
        var index = ReadingRowIndex.Build([file]);
        var overlay = ReadingPlanCompiler
            .Compile(index, new ReadingPlan([], [], [new ReadingFold(2, 3), new ReadingFold(4, 6)]))
            .Overlay!;

        var set = Flatten(file, overlay);
        var fold = Assert.Single(set.Rows.OfType<DiffRow.Fold>());

        Assert.Equal(5, fold.HiddenCount);
        Assert.Equal([2, 4], fold.StartRows);
        Assert.Equal(["keep", "tail"], LineTexts(set));

        var reopened = Flatten(file, overlay, expandedFolds: [.. fold.StartRows]);
        Assert.Empty(reopened.Rows.OfType<DiffRow.Fold>());
        Assert.Equal(["keep", "a1", "a2", "b1", "b2", "b3", "tail"], LineTexts(reopened));
    }

    // Folds on opposite sides of the diff stay apart: merging a removed run into an added one would
    // draw a single ellipsis standing for both, which reads as one edit rather than two.
    [Fact]
    public void KeepsFoldsOfDifferentSidesSeparate()
    {
        var file = File("a.txt", Hunk(
            new DiffLine(DiffLineKind.Removed, 1, null, "r1"),
            new DiffLine(DiffLineKind.Removed, 2, null, "r2"),
            Add(1, "a1"),
            Add(2, "a2")));
        var index = ReadingRowIndex.Build([file]);
        var overlay = ReadingPlanCompiler
            .Compile(index, new ReadingPlan([], [], [new ReadingFold(1, 2), new ReadingFold(3, 4)]))
            .Overlay!;

        var folds = Flatten(file, overlay).Rows.OfType<DiffRow.Fold>().ToArray();

        Assert.Equal(2, folds.Length);
        Assert.Equal(DiffLineKind.Removed, folds[0].Kind);
        Assert.Equal(DiffLineKind.Added, folds[1].Kind);
    }

    // Clicking a fold gives the source back without disturbing the rest of the plan.
    [Fact]
    public void ExpandingAFoldRestoresItsRowsOnly()
    {
        var file = File("a.txt", Hunk(
            Add(1, "keep"),
            Add(2, "hidden a"),
            Add(3, "hidden b"),
            Add(4, "gone")));
        var index = ReadingRowIndex.Build([file]);
        var overlay = ReadingPlanCompiler
            .Compile(index, new ReadingPlan([new ReadingRemoval(4, 4)], [], [new ReadingFold(2, 3)]))
            .Overlay!;

        var set = Flatten(file, overlay, expandedFolds: 2);

        Assert.Empty(set.Rows.OfType<DiffRow.Fold>());
        Assert.Equal(["keep", "hidden a", "hidden b"], LineTexts(set));
    }

    [Fact]
    public void DrawsAnElidedRowWithItsShortenedText()
    {
        var file = File("a.go", Hunk(Add(1, "    t.Errorf(\"want %d\", got)")));
        var index = ReadingRowIndex.Build([file]);
        var overlay = ReadingPlanCompiler
            .Compile(index, new ReadingPlan([], [new ReadingElision(1, "\"want %d\", got", "…")], []))
            .Overlay!;

        Assert.Equal(["    t.Errorf(…)"], LineTexts(Flatten(file, overlay)));
    }

    // A hunk the plan emptied drops its separator too; leaving the @@ bar would point at nothing.
    [Fact]
    public void DropsTheChromeOfAHunkWithNothingLeftToShow()
    {
        var file = File("a.txt",
            Hunk(Add(1, "gone one"), Add(2, "gone two")),
            Hunk(Add(9, "kept")));
        var index = ReadingRowIndex.Build([file]);
        var overlay = ReadingPlanCompiler.Compile(index, new ReadingPlan([new ReadingRemoval(1, 2)], [], [])).Overlay!;

        var set = Flatten(file, overlay);

        Assert.Single(set.Rows.OfType<DiffRow.HunkSeparator>());
        Assert.Equal(["kept"], LineTexts(set));
    }

    // The plan describes a set of files; a diff outside it renders untouched rather than being
    // silently misindexed against another file's coordinates.
    [Fact]
    public void IgnoresAnOverlayThatDoesNotCoverThisFile()
    {
        var planned = File("a.txt", Hunk(Add(1, "one"), Add(2, "two")));
        var other = File("b.txt", Hunk(Add(1, "alpha"), Add(2, "beta")));
        var index = ReadingRowIndex.Build([planned]);
        var overlay = ReadingPlanCompiler.Compile(index, new ReadingPlan([new ReadingRemoval(1, 2)], [], [])).Overlay!;

        Assert.Equal(["alpha", "beta"], LineTexts(Flatten(other, overlay)));
    }
}
