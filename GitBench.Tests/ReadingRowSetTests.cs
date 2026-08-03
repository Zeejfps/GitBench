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
