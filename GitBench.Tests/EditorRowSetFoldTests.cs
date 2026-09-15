using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>Folds and usages rows in the editable projection, including while the parse is behind the document.</summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class EditorRowSetFoldTests(CodeIntelFixture fixture)
{
    private const string Path = "src/AuthService.cs";

    private static readonly string[] Source =
    [
        "class AuthService",
        "{",
        "    void Login(string user)",
        "    {",
        "        Check(user);",
        "        Issue(user);",
        "    }",
        "",
        "    void Logout() => Done();",
        "}",
    ];

    [Fact]
    public void CollapsingADeclarationHidesItsBodyAndChipsTheLineAbove()
    {
        var (_, rows) = Folded("Login(string)");

        Assert.Equal(Source.Length - 4, rows.Rows.Count);
        Assert.Equal("    void Login(string user)", Raw(rows, 2));
        Assert.True(Line(rows, 2).Fold is { Chip: true });
        Assert.Equal(string.Empty, Raw(rows, 3));
        Assert.Equal(new FileLine(8), Line(rows, 3).NewNumber.Line!.Value);
    }

    [Fact]
    public void ADeclarationWithAFoldableBodyCarriesTheChevron()
    {
        var (_, rows) = Open();

        var chevrons = rows.Rows.OfType<DiffRow.Line>().Where(r => r.Fold is { Chevron: true }).ToList();
        Assert.Equal(2, chevrons.Count);
        Assert.Equal("class AuthService", chevrons[0].Text.Raw);
        Assert.Equal("    void Login(string user)", chevrons[1].Text.Raw);
    }

    [Fact]
    public void AFoldColumnIsReservedByHavingAFoldSetRatherThanByAnythingBeingFolded()
    {
        var (_, plain) = Projection(folds: null);
        var (_, foldable) = Open();

        Assert.False(plain.FoldColumn);
        Assert.True(foldable.FoldColumn);
        Assert.Null(plain.HiddenText);
        Assert.NotNull(foldable.HiddenText);
    }

    [Fact]
    public void CopyingAcrossAFoldReInflatesTheTextItSwallowed()
    {
        var (_, rows) = Folded("Login(string)");

        var hidden = rows.HiddenText!(new RowIndex(2));

        Assert.Equal("    {\n        Check(user);\n        Issue(user);\n    }", hidden);
    }

    [Fact]
    public void TheUsagesRowsSitAboveTheDeclarationsTheyCountFor()
    {
        var (_, rows) = Projection(folds: null, usageLens: true);

        var lenses = rows.Rows.OfType<DiffRow.Lens>().ToList();
        Assert.Equal(3, lenses.Count);
        Assert.Equal(new FileLine(1), lenses[0].At);
        Assert.Equal(new FileLine(3), lenses[1].At);
        Assert.Equal(new FileLine(9), lenses[2].At);
        Assert.Equal(Source.Length + 3, rows.Rows.Count);
        Assert.Equal("    void Login(string user)", Raw(rows, 4));
    }

    [Fact]
    public void AFoldedDeclarationHidesItsMembersUsagesRowsWithThem()
    {
        var (_, rows) = Projection(folds: Collapsed("AuthService"), usageLens: true);

        var lenses = rows.Rows.OfType<DiffRow.Lens>().ToList();
        Assert.Single(lenses);
        Assert.Equal(new FileLine(1), lenses[0].At);
    }

    [Fact]
    public void ALensRowNamesNoLineSoItAnchorsOnTheOneAboveIt()
    {
        var (_, rows) = Projection(folds: null, usageLens: true);

        Assert.Null(rows.NewLineAt(new RowIndex(3)));
        Assert.Equal(new FileLine(3), rows.NewLineAt(new RowIndex(4))!.Value);
        Assert.Equal(new DiffRowAnchor(DiffRowKey.NewSide(new FileLine(2)), 1),
            rows.AnchorAt(new RowIndex(3)));
        Assert.Equal(new RowIndex(3), rows.RowAt(new DiffRowAnchor(DiffRowKey.NewSide(new FileLine(2)), 1)));
    }

    [Fact]
    public void ALineBehindAFoldHasNoRowButStillHasSomewhereToScroll()
    {
        var (_, rows) = Folded("Login(string)");

        Assert.Null(rows.RowForNewLine(new FileLine(5)));
        Assert.Equal(new RowIndex(2), rows.RowNearestNewLine(new FileLine(5)));
        Assert.Equal(new RowIndex(3), rows.RowForNewLine(new FileLine(8)));
    }

    [Fact]
    public void EveryVisibleLineRoundTripsThroughItsRow()
    {
        var (_, rows) = Projection(folds: Collapsed("Login(string)"), usageLens: true);

        for (var line = 1; line <= Source.Length; line++)
        {
            if (rows.RowForNewLine(new FileLine(line)) is not { } row) continue;
            Assert.Equal(new FileLine(line), rows.NewLineAt(row)!.Value);
        }
    }

    [Fact]
    public void FoldingTheLongestLineOutOfSightBringsTheWidthDown()
    {
        var (_, open) = Open();
        var (_, folded) = Folded("AuthService");

        Assert.Equal(DiffText.VisualCells("    void Logout() => Done();"), open.MaxRowCells);
        Assert.Equal(
            DiffText.VisualCells("class AuthService") + DiffText.VisualCells(FullFileRow.FoldChipText),
            folded.MaxRowCells);
    }

    [Fact]
    public void UnfoldingPutsTheWidthBack()
    {
        var (_, rows) = Open();
        var wide = rows.MaxRowCells;

        rows.SetFolds(Collapsed("AuthService"));
        rows.SetFolds(FoldState.Open(Path));

        Assert.Equal(wide, rows.MaxRowCells);
        Assert.Equal(Source.Length, rows.Rows.Count);
    }

    [Fact]
    public void ALensRowIsAllowedForInTheHorizontalExtent()
    {
        var (_, rows) = Projection(Collapsed("AuthService"), usageLens: true);

        Assert.Equal(
            Math.Max(
                DiffText.VisualCells("class AuthService") + DiffText.VisualCells(FullFileRow.FoldChipText),
                FullFileRow.UsageLensCells),
            rows.MaxRowCells);
    }

    [Fact]
    public void AParseOfAnOlderDocumentIsRefusedRatherThanApplied()
    {
        var document = TextDocument.FromText(string.Join('\n', Source));
        var rows = new EditorRowSet(document, Loc());
        var parsed = Annotations(document);

        rows.Reproject(document.Apply(new TextEdit(
            TextRange.Caret(TextPosition.At(1, 0)), "// a comment\n")));

        Assert.False(rows.SetAnnotations(parsed));
        Assert.All(rows.Rows, row => Assert.Null(Assert.IsType<DiffRow.Line>(row).Fold));
    }

    [Fact]
    public void AFoldFollowsTheLinesAnEditAboveItMoved()
    {
        var (document, rows) = Folded("Login(string)");
        Assert.Equal("    void Login(string user)", Raw(rows, 2));

        rows.Reproject(document.Apply(new TextEdit(
            TextRange.Caret(TextPosition.At(1, 0)), "// a comment\n")));

        Assert.Equal("// a comment", Raw(rows, 0));
        Assert.Equal("    void Login(string user)", Raw(rows, 3));
        Assert.True(Line(rows, 3).Fold is { Chip: true });
        Assert.Equal(string.Empty, Raw(rows, 4));
        Assert.Null(rows.RowForNewLine(new FileLine(6)));
    }

    [Fact]
    public void ALineTypedInsideADeclarationGrowsTheFoldBelowItRatherThanBreakingIt()
    {
        var (document, rows) = Folded("AuthService");
        var before = rows.Rows.Count;

        rows.Reproject(document.Apply(new TextEdit(
            TextRange.Caret(TextPosition.At(1, 17)), "Base")));

        Assert.Equal(before, rows.Rows.Count);
        Assert.Equal("class AuthServiceBase", Raw(rows, 0));
        Assert.True(Line(rows, 0).Fold is { Chip: true });
    }

    [Fact]
    public void ADeleteReachingIntoACollapsedRangeShrinksTheFoldRatherThanMisplacingIt()
    {
        var (document, rows) = Folded("Login(string)");

        rows.Reproject(document.Apply(new TextEdit(
            new TextRange(TextPosition.At(3, 5), TextPosition.At(6, 0)), string.Empty)));

        Assert.Equal(4, rows.Rows.Count);
        Assert.True(Line(rows, 2).Fold is { Chip: true });
        Assert.Equal(
            string.Join('\n', Enumerable.Range(4, 3).Select(n => document.Line(new FileLine(n)))),
            rows.HiddenText!(new RowIndex(2)));
    }

    [Fact]
    public void FoldingAfterTypingCollapsesTheLinesTheDeclarationIsOnNow()
    {
        var (document, rows) = Open();

        rows.Reproject(document.Apply(new TextEdit(
            TextRange.Caret(TextPosition.At(1, 0)), "// one\n// two\n")));
        rows.SetFolds(Collapsed("Login(string)"));

        Assert.Equal("    void Login(string user)", Raw(rows, 4));
        Assert.True(Line(rows, 4).Fold is { Chip: true });
        Assert.Equal(string.Empty, Raw(rows, 5));
        Assert.Equal(
            "    {\n        Check(user);\n        Issue(user);\n    }",
            rows.HiddenText!(new RowIndex(4)));
    }

    [Fact]
    public void AKeystrokeOnAFoldedFileStillMeasuresOneLine()
    {
        var (document, rows) = Folded("Login(string)");
        var measured = rows.LineMeasurements;

        rows.Reproject(document.Apply(new TextEdit(TextRange.Caret(TextPosition.At(1, 5)), "x")));

        Assert.Equal(measured + 1, rows.LineMeasurements);
        Assert.Equal(0, rows.Resynchronizations);
    }

    [Fact]
    public void AFileWithNothingToFoldKeepsTheRowsItHadBuiltAcrossANewline()
    {
        var (document, rows) = Projection(folds: null);
        var before = rows.Rows.Take(3).ToArray();
        var measured = rows.LineMeasurements;

        rows.Reproject(document.Apply(new TextEdit(TextRange.Caret(TextPosition.At(9, 0)), "\n")));

        Assert.Equal(measured + 2, rows.LineMeasurements);
        for (var i = 0; i < before.Length; i++) Assert.Same(before[i], rows.Rows[i]);
    }

    [Fact]
    public void AKeystrokeOnAFoldedFileLeavesTheRowsAboveItAlone()
    {
        var (document, rows) = Folded("Login(string)");
        var before = rows.Rows[0];

        rows.Reproject(document.Apply(new TextEdit(TextRange.Caret(TextPosition.At(2, 0)), "x")));

        Assert.Same(before, rows.Rows[0]);
    }

    [Fact]
    public void ACaretSteeredIntoACollapsedDeclarationOpensIt()
    {
        var (document, rows) = Folded("Login(string)");
        var opened = new List<string>();
        rows.FoldExpanded = opened.Add;

        Assert.True(rows.Reveal(new FileLine(5)));

        Assert.Equal(new[] { "AuthService.Login(string)" }, opened);
        Assert.Equal(Source.Length, rows.Rows.Count);
        Assert.Equal(new RowIndex(4), rows.RowForNewLine(new FileLine(5)));
    }

    [Fact]
    public void ACaretOnAVisibleLineOpensNothing()
    {
        var (_, rows) = Folded("Login(string)");
        rows.FoldExpanded = _ => throw new InvalidOperationException("nothing should have opened");

        Assert.False(rows.Reveal(new FileLine(3)));
        Assert.Equal(Source.Length - 4, rows.Rows.Count);
    }

    [Fact]
    public async Task ReadingTheRowsFromAnotherThreadIsRefusedRatherThanRaced()
    {
        var (_, rows) = Open();

        var thrown = await Task.Run(() => Record.Exception(() => rows.Rows[0]));

        Assert.IsType<InvalidOperationException>(thrown);
    }

    [Fact]
    public async Task HandingAnAnnotationOverFromAnotherThreadIsRefusedToo()
    {
        var (document, rows) = Open();
        var parsed = Annotations(document);

        var thrown = await Task.Run(() => Record.Exception(() => rows.SetAnnotations(parsed)));

        Assert.IsType<InvalidOperationException>(thrown);
    }

    private (TextDocument Document, EditorRowSet Rows) Open() => Projection(FoldState.Open(Path));

    private (TextDocument Document, EditorRowSet Rows) Folded(string declaration) =>
        Projection(Collapsed(declaration));

    private (TextDocument Document, EditorRowSet Rows) Projection(
        FoldState? folds, bool usageLens = false)
    {
        var document = TextDocument.FromText(string.Join('\n', Source));
        var rows = new EditorRowSet(document, Loc()) { UsageLensRows = usageLens };
        rows.SetFolds(folds);
        rows.SetAnnotations(Annotations(document));
        return (document, rows);
    }

    private Revised<DiffAnnotations> Annotations(TextDocument document) =>
        new(DocumentRevision.Of(document),
            new DiffAnnotations(null, fixture.Outline(string.Join('\n', Source)), null));

    private static FoldState Collapsed(string declaration) =>
        FoldState.Open(Path).Toggled(declaration == "AuthService" ? "AuthService" : $"AuthService.{declaration}");

    private static DiffRow.Line Line(EditorRowSet rows, int index) =>
        Assert.IsType<DiffRow.Line>(rows.Rows[index]);

    private static string Raw(EditorRowSet rows, int index) => Line(rows, index).Text.Raw;

    private static ILocalizationService Loc() => new LocalizationService(new State<Locale>(Locale.En));
}
