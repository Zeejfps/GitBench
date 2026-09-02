using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// Folding a declaration's body out of the whole-file viewer. The outline decides what folds, so
/// these parse real source rather than hand-building an outline — the predicate this rests on
/// (<c>SignatureEndLine &lt; EndLine</c>) is only total because the extractor makes it so.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public class FoldingTests(CodeIntelFixture fixture)
{
    private const string Source = """
        namespace App;

        class AuthService
        {
            void Login(string user)
            {
                Check(user);
                Issue(user);
            }

            void Abstracted() => Login("x");
        }
        """;

    [Fact]
    public void ADeclarationWithABodyCarriesAChevronAndOneWithoutDoesNot()
    {
        var rows = Rows(Open());

        Assert.Equal("    void Login(string user)", TextAt(rows, ChevronRows(rows).Last()));
        // The class and the method fold; the namespace declares on one line and the
        // expression-bodied member has no body, so neither does.
        Assert.Equal(2, ChevronRows(rows).Count);
    }

    // The braces go with the body, so a folded declaration reads as the one line it declares on.
    [Fact]
    public void CollapsingLeavesTheDeclarationOnASingleLineCarryingTheChip()
    {
        var rows = Rows(Collapsed("App.AuthService.Login(string)"));

        Assert.Contains(rows, r => Text(r) == "    void Login(string user)");
        Assert.DoesNotContain(rows, r => Text(r) == "    {");
        Assert.DoesNotContain(rows, r => Text(r) == "        Check(user);");
        Assert.DoesNotContain(rows, r => Text(r) == "        Issue(user);");
        Assert.DoesNotContain(rows, r => Text(r) == "    }");

        var chip = Assert.Single(rows.OfType<DiffRow.Line>().Where(r => r.Fold is { Chip: true }));
        Assert.Equal("    void Login(string user)", chip.Text.Raw);
        Assert.True(chip.Fold!.Value.Chevron, "the one visible row carries both the toggle and the chip");
    }

    // The gutter is the file's own numbering, so it has to keep counting past what is hidden.
    [Fact]
    public void LineNumbersBelowACollapsedFoldAreUnchanged()
    {
        const string below = "    void Abstracted() => Login(\"x\");";

        Assert.Equal(11, NumberOf(Rows(Open()), below));
        Assert.Equal(11, NumberOf(Rows(Collapsed("App.AuthService.Login(string)")), below));
    }

    [Fact]
    public void CollapsingTheContainingClassTakesItsMethodsWithIt()
    {
        var rows = Rows(Collapsed("App.AuthService"));

        Assert.Contains(rows, r => Text(r) == "class AuthService");
        Assert.DoesNotContain(rows, r => Text(r) == "    void Login(string user)");
        // Outermost wins: the method's own chevron is inside the hidden range, so it is not emitted.
        Assert.Single(ChevronRows(rows));
    }

    [Fact]
    public void WithNoFoldStateNothingIsMarkedAndNoColumnIsReserved()
    {
        var set = Set(null);

        Assert.False(set.FoldColumn);
        Assert.All(set.Rows.OfType<DiffRow.Line>(), r => Assert.Null(r.Fold));
    }

    [Fact]
    public void CopyingAcrossACollapsedFoldBringsTheBodyWithIt()
    {
        var set = Set(Collapsed("App.AuthService.Login(string)"));
        var span = DiffSelectionModel.WholeSpan(set.Rows);
        Assert.NotNull(span);

        var text = DiffSelectionModel.BuildCopyText(
            set.Rows, span.Value.Start, span.Value.End, set.HiddenAfter);

        Assert.Contains("Check(user);", text);
        Assert.Contains("Issue(user);", text);
        // The braces are hidden now too, so they have to come back with the body.
        Assert.Contains("    {", text);
    }

    // Ending the selection on the fold's own row selects the row, not the body behind it.
    [Fact]
    public void ASelectionThatStopsAtTheFoldDoesNotDragTheBodyIn()
    {
        var set = Set(Collapsed("App.AuthService.Login(string)"));
        var chipRow = set.Rows
            .Select((row, index) => (row, index))
            .First(r => r.row is DiffRow.Line { Fold.Chip: true }).index;

        var text = DiffSelectionModel.BuildCopyText(
            set.Rows, new DiffTextPos(default, default),
            new DiffTextPos(new RowIndex(chipRow), new ExpandedColumn(5)),
            set.HiddenAfter);

        Assert.DoesNotContain("Check(user);", text);
    }

    private static FoldState Open() => FoldState.Open(Path);

    private static FoldState Collapsed(string id) => FoldState.Open(Path).Toggled(id);

    private const string Path = "src/AuthService.cs";

    private IReadOnlyList<DiffRow> Rows(FoldState? folds) => Set(folds).Rows;

    private DiffRowSet Set(FoldState? folds)
    {
        var lines = Source.ReplaceLineEndings("\n").Split('\n');
        var state = new DiffRenderState.FullFile(
            Path,
            lines,
            AddedLineNumbers: new HashSet<int>(),
            Side: DiffSide.WorkingTree,
            Truncated: false,
            Emphasis: null,
            Annotations: new DiffAnnotations(null, fixture.Outline(Source), null));
        return DiffRowSet.Build(state, new LocalizationService(new State<Locale>(Locale.En)), folds);
    }

    private static IReadOnlyList<int> ChevronRows(IReadOnlyList<DiffRow> rows) =>
        rows.Select((row, index) => (row, index))
            .Where(r => r.row is DiffRow.Line { Fold.Chevron: true })
            .Select(r => r.index)
            .ToArray();

    private static string TextAt(IReadOnlyList<DiffRow> rows, int index) => Text(rows[index]);

    private static string Text(DiffRow row) => row is DiffRow.Line line ? line.Text.Raw : string.Empty;

    private static int NumberOf(IReadOnlyList<DiffRow> rows, string text) =>
        rows.OfType<DiffRow.Line>().Single(r => r.Text.Raw == text).NewNumber.Line!.Value.Value;
}
