using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// Tabs on the way out of a diff. The body renders them as spaces — that is what keeps syntax
/// colors sitting on their glyphs — but a copy is file text: a Go function that pastes without its
/// indentation is annoying, and a Makefile recipe whose tab came back as spaces is a file make
/// refuses to run.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class DiffTabCopyTests(CodeIntelFixture fixture)
{
    // ---- the row stream keeps both forms ----

    [Fact]
    public void FlattenedRowsRenderTabsAsSpacesAndKeepTheFileText()
    {
        var rows = FullFileRows("Makefile", "build:", "\tgcc -o app main.c");

        Assert.Equal("\tgcc -o app main.c", Line(rows, 1).Text.Raw);
        Assert.Equal(new string(' ', DiffOptions.TabWidth) + "gcc -o app main.c", Line(rows, 1).Text.Expanded);
    }

    // The worst failure mode there is: make requires a literal tab, so a pasted recipe indented
    // with spaces is a syntax error.
    [Fact]
    public void AMakefileRecipeCopiesWithTheTabMakeRequires()
    {
        var rows = FullFileRows("Makefile", "build:", "\tgcc -o app main.c");

        Assert.Equal("build:\n\tgcc -o app main.c", CopyAll(rows));
    }

    [Fact]
    public void ADiffHunksLinesCopyWithTheirTabs()
    {
        var diff = DiffOf(Hunk(
            1, 2, 1, 2, null,
            new DiffLine(DiffLineKind.Context, 1, 1, "func main() {"),
            new DiffLine(DiffLineKind.Added, null, 2, "\tfmt.Println(\"hi\")")));
        var rows = DiffRowSet.Build(new DiffRenderState.Loaded(diff, null), Loc()).Rows;

        Assert.Equal("func main() {\n\tfmt.Println(\"hi\")", CopyAll(rows));
    }

    // ---- shapes of indentation ----

    [Fact]
    public void MixedLeadingTabsAndSpacesAreCopiedExactly()
    {
        var rows = FullFileRows("src/app.go", "\t  \tdeeply(indented)");

        Assert.Equal("\t  \tdeeply(indented)", CopyAll(rows));
    }

    [Fact]
    public void ATabInTheMiddleOfALineSurvives()
    {
        var rows = FullFileRows("src/data.tsv", "name\tvalue");

        Assert.Equal("name\tvalue", CopyAll(rows));
    }

    [Fact]
    public void ConsecutiveTabsAreAllKept()
    {
        var rows = FullFileRows("src/app.go", "\t\t\treturn nil");

        Assert.Equal("\t\t\treturn nil", CopyAll(rows));
    }

    [Fact]
    public void ALineWithNoTabsIsCopiedUnchanged()
    {
        var rows = FullFileRows("src/App.cs", "    var total = Compute();");

        Assert.Equal("    var total = Compute();", CopyAll(rows));
    }

    // ---- selections that land inside a tab ----

    // The caret can sit between the spaces a tab drew, but the tab itself is one character with no
    // inside. A selection that covers any of it takes it whole — the alternative is a paste that
    // quietly loses a level of indentation.
    [Fact]
    public void ASelectionStartingInsideATabsSpacesTakesTheTab()
    {
        var rows = FullFileRows("src/app.go", "\t\treturn nil");

        Assert.Equal("\t\treturn nil", Copy(rows, At(0, 1), At(0, 100)));
    }

    [Fact]
    public void ASelectionEndingInsideATabsSpacesTakesTheTab()
    {
        var rows = FullFileRows("src/app.go", "\t\treturn nil");

        // Two columns into the second tab's four.
        Assert.Equal("\t\t", Copy(rows, At(0, 0), At(0, DiffOptions.TabWidth + 2)));
    }

    [Fact]
    public void ASelectionClearOfTheIndentationLeavesItBehind()
    {
        var rows = FullFileRows("src/app.go", "\t\treturn nil");

        Assert.Equal("return nil", Copy(rows, At(0, DiffOptions.TabWidth * 2), At(0, 100)));
    }

    // ---- multi-row ----

    [Fact]
    public void AMultiRowSelectionKeepsEachRowsOwnIndentation()
    {
        var rows = FullFileRows("src/app.go", "func f() {", "\tif x {", "\t\treturn 1", "\t}", "}");

        Assert.Equal("func f() {\n\tif x {\n\t\treturn 1\n\t}\n}", CopyAll(rows));
    }

    // The start and end columns are resolved against their own rows, which have different tab
    // counts — nothing may be measured against the first row's expansion.
    [Fact]
    public void APartialSelectionAcrossRowsOfDifferentDepthsResolvesPerRow()
    {
        var rows = FullFileRows("src/app.go", "\tif x {", "\t\treturn 1", "\t}");

        Assert.Equal(
            "\tif x {\n\t\treturn 1\n\t}",
            Copy(rows, At(0, 2), At(2, DiffOptions.TabWidth + 1)));
    }

    // ---- non-ASCII, since offsets are UTF-16 code units ----

    [Fact]
    public void AnEmojiOnATabIndentedLineDoesNotShiftTheTabBack()
    {
        var rows = FullFileRows("src/app.go", "\tfmt.Println(\"🎉 done\")");

        Assert.Equal("\tfmt.Println(\"🎉 done\")", CopyAll(rows));
        // Past the tab, through the surrogate pair, and one character beyond it.
        Assert.Equal("fmt.Println(\"🎉 ", Copy(rows, At(0, DiffOptions.TabWidth), At(0, DiffOptions.TabWidth + 16)));
    }

    // ---- rendering still counts in expanded columns ----

    // The fix must not push raw text at the painter: the highlight over a tab has to cover the four
    // cells it drew, not the one character behind them.
    [Fact]
    public void TheHighlightSpanStaysInExpandedColumns()
    {
        var rows = FullFileRows("src/app.go", "\treturn nil");
        var line = Line(rows, 0);
        var selection = new DiffSelectionModel();
        selection.SetRange(null, At(0, 0), new DiffTextPos(default, line.Text.End));

        Assert.True(selection.TryRowSpan(null, default, line.Text.End, out var span));
        Assert.Equal(new ExpandedColumn(0), span.StartChar);
        Assert.Equal(new ExpandedColumn("return nil".Length + DiffOptions.TabWidth), span.EndChar);
    }

    // ---- the fold body a copy drags in ----

    [Fact]
    public void AFoldSwallowedBodyIsReInflatedWithItsTabs()
    {
        string[] lines =
        [
            "class AuthService",
            "{",
            "\tvoid Login(string user)",
            "\t{",
            "\t\tCheck(user);",
            "\t}",
            "}",
        ];
        var open = FoldSet(lines, FoldState.Open(FoldPath));
        var methodId = open.Rows.OfType<DiffRow.Line>().Last(r => r.Fold is { Chevron: true }).Fold!.Value.Id;

        var collapsed = FoldSet(lines, FoldState.Open(FoldPath).Toggled(methodId));
        Assert.True(collapsed.Rows.Count < open.Rows.Count, "the body has to actually be hidden");

        var text = DiffSelectionModel.BuildCopyText(
            collapsed.Rows, At(0, 0), At(collapsed.Rows.Count - 1, 100), collapsed.HiddenAfter);

        Assert.Equal(string.Join('\n', lines), text);
    }

    // ---- the assistant sees what the clipboard sees ----

    [Fact]
    public void TheAssistantQuoteCarriesTheSameTabsAsTheClipboard()
    {
        var rows = FullFileRows("src/app.go", "func f() {", "\treturn 1", "}");
        var quote = DiffSelectionQuote.Build(rows, At(0, 0), At(2, 1), "src/app.go");

        Assert.NotNull(quote);
        Assert.Equal(CopyAll(rows), quote.Text);
        Assert.Contains("\treturn 1", quote.ToPrompt(null), StringComparison.Ordinal);
    }

    // ---- helpers ----

    private const string FoldPath = "src/AuthService.cs";

    private static DiffTextPos At(int row, int column) => new(new RowIndex(row), new ExpandedColumn(column));

    private static DiffRow.Line Line(IReadOnlyList<DiffRow> rows, int index) =>
        Assert.IsType<DiffRow.Line>(rows[index]);

    private static string CopyAll(IReadOnlyList<DiffRow> rows)
    {
        var span = DiffSelectionModel.WholeSpan(rows);
        Assert.NotNull(span);
        return DiffSelectionModel.BuildCopyText(rows, span.Value.Start, span.Value.End);
    }

    private static string Copy(IReadOnlyList<DiffRow> rows, DiffTextPos start, DiffTextPos end) =>
        DiffSelectionModel.BuildCopyText(rows, start, end);

    private static ILocalizationService Loc() => new LocalizationService(new State<Locale>(Locale.En));

    private static IReadOnlyList<DiffRow> FullFileRows(string path, params string[] lines) =>
        DiffRowSet.Build(
            new DiffRenderState.FullFile(
                path, lines, new HashSet<int>(), DiffSide.WorkingTree, false, null, null),
            Loc()).Rows;

    private DiffRowSet FoldSet(IReadOnlyList<string> lines, FoldState folds) =>
        DiffRowSet.Build(
            new DiffRenderState.FullFile(
                FoldPath, lines, new HashSet<int>(), DiffSide.WorkingTree, false, null,
                new DiffAnnotations(null, fixture.Outline(string.Join('\n', lines)), null)),
            Loc(),
            folds);

    private static DiffResult DiffOf(params DiffHunk[] hunks) => new(
        RepoId: Guid.Empty,
        Path: "main.go",
        OldPath: null,
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
