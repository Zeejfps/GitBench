using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>What a row is called across a rebuild, and where a position taken against the old stream lands in the new one.</summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class DiffRowIdentityTests(CodeIntelFixture fixture)
{
    private static DiffRowSet Diff(ContextExpansion? expansion = null) => DiffRowSet.Build(
        new DiffRenderState.Loaded(TwoHunks(), null, expansion), Loc());

    private static ContextExpansion ExpandedGap1()
    {
        var lines = new List<string>();
        for (var n = 1; n <= 20; n++) lines.Add($"line {n}");
        return new ContextExpansion(lines, Truncated: false, new Dictionary<int, GapShown> { [1] = new(2, 0) });
    }

    [Fact]
    public void ATextRowIsNamedByTheLineItSurvivesTheChangeOn()
    {
        var set = Diff();

        Assert.Equal(Anchor(DiffRowKey.NewSide(new FileLine(10))), set.AnchorAt(new RowIndex(1)));
        Assert.Equal(Anchor(DiffRowKey.NewSide(new FileLine(11))), set.AnchorAt(new RowIndex(3)));
    }

    [Fact]
    public void ARemovedRowIsNamedByItsBeforeSideLine()
    {
        var set = Diff();

        Assert.Equal(Anchor(DiffRowKey.OldSide(new FileLine(11))), set.AnchorAt(new RowIndex(2)));
        Assert.NotEqual(set.AnchorAt(new RowIndex(2)), set.AnchorAt(new RowIndex(3)));
    }

    [Fact]
    public void ChromeAnchorsOnTheTextRowAboveIt()
    {
        var set = Diff();

        Assert.Equal(new DiffRowAnchor(DiffRowKey.NewSide(new FileLine(12)), 1), set.AnchorAt(new RowIndex(5)));
    }

    [Fact]
    public void ChromeWithNoTextRowAboveItAnchorsOnTheTopOfTheStream()
    {
        Assert.Equal(new DiffRowAnchor(null, 1), Diff().AnchorAt(new RowIndex(0)));
    }

    [Fact]
    public void ARowOutsideTheStreamHasNoAnchor()
    {
        var set = Diff();

        Assert.Null(set.AnchorAt(new RowIndex(-1)));
        Assert.Null(set.AnchorAt(new RowIndex(999)));
    }

    [Fact]
    public void EveryRowFindsItsOwnWayBack()
    {
        foreach (var set in new[] { Diff(), Diff(ExpandedGap1()), FullFile(null) })
            for (var i = 0; i < set.Rows.Count; i++)
            {
                var row = new RowIndex(i);
                Assert.Equal(row, set.RowAt(set.AnchorAt(row)!.Value));
            }
    }

    [Fact]
    public void AReflattenOfTheSameStateLeavesEveryPositionWhereItWas()
    {
        var before = Diff();
        var after = Diff();

        for (var i = 0; i < before.Rows.Count; i++)
        {
            var pos = At(i, 3);
            Assert.Equal(pos, Mapped(before, after, pos));
        }
    }

    [Fact]
    public void AGapExpansionLeavesEveryTextRowOnItsOwnLine()
    {
        var before = Diff();
        var after = Diff(ExpandedGap1());

        for (var i = 0; i < before.Rows.Count; i++)
        {
            var row = new RowIndex(i);
            if (before.NewLineAt(row) is not { } line) continue;

            var mapped = Mapped(before, after, At(i, 7));
            Assert.NotNull(mapped);
            Assert.Equal(line, after.NewLineAt(mapped.Value.Row));
            Assert.Equal(new ExpandedColumn(7), mapped.Value.Char);
        }
    }

    [Fact]
    public void AnEndpointOnABarThatAnExpansionDissolvedFallsBackToItsOwnLine()
    {
        var before = Diff();
        var after = Diff(ExpandedGap1());

        var mapped = Mapped(before, after, At(5, 0));

        Assert.NotNull(mapped);
        Assert.Equal(new FileLine(12), after.NewLineAt(mapped.Value.Row));
        Assert.Equal(new FileLine(13), after.NewLineAt(new RowIndex(mapped.Value.Row.Value + 1)));
    }

    [Fact]
    public void ALineACollapsedFoldSwallowedMapsNowhere()
    {
        var open = FullFile(FoldState.Open(Path));
        var collapsed = FullFile(Collapsed(open));
        var body = RowOf(open, new FileLine(5));

        Assert.Null(Mapped(open, collapsed, new DiffTextPos(body, new ExpandedColumn(8))));
    }

    [Fact]
    public void ALineBelowACollapsedFoldKeepsItsColumnOnItsOwnLine()
    {
        var open = FullFile(FoldState.Open(Path));
        var collapsed = FullFile(Collapsed(open));

        var mapped = Mapped(open, collapsed, new DiffTextPos(RowOf(open, new FileLine(9)), new ExpandedColumn(9)));

        Assert.NotNull(mapped);
        Assert.Equal(new FileLine(9), collapsed.NewLineAt(mapped.Value.Row));
        Assert.Equal(new ExpandedColumn(9), mapped.Value.Char);
    }

    [Fact]
    public void ExpandingAFoldPutsItsBodyBackWhereItWas()
    {
        var open = FullFile(FoldState.Open(Path));
        var collapsed = FullFile(Collapsed(open));

        var mapped = Mapped(collapsed, open, new DiffTextPos(RowOf(collapsed, new FileLine(9)), default));

        Assert.NotNull(mapped);
        Assert.Equal(RowOf(open, new FileLine(9)), mapped.Value.Row);
    }

    [Fact]
    public void RemappingMovesBothEndsAndReportsTheChange()
    {
        var selection = new DiffSelectionModel();
        selection.SetRange(null, At(4, 2), At(8, 6));

        Assert.True(selection.Remap(p => At(p.Row.Value - 2, p.Char.Value)));
        Assert.Equal(At(2, 2), selection.Anchor);
        Assert.Equal(At(6, 6), selection.Focus);
    }

    [Fact]
    public void AnEndpointWithNowhereToGoTakesTheWholeSelectionWithIt()
    {
        var selection = new DiffSelectionModel();
        selection.SetRange(null, At(4, 2), At(8, 6));

        Assert.True(selection.Remap(p => p.Row.Value == 8 ? null : p));

        Assert.False(selection.IsActive);
        Assert.False(selection.HasRange);
    }

    [Fact]
    public void AMappingThatMovesNothingIsNotAChange()
    {
        var selection = new DiffSelectionModel();
        selection.SetRange(null, At(4, 2), At(8, 6));

        Assert.False(selection.Remap(p => p));
    }

    [Fact]
    public void RemappingWithNothingSelectedDoesNothing()
    {
        var selection = new DiffSelectionModel();

        Assert.False(selection.Remap(_ => throw new InvalidOperationException("nothing to map")));
    }

    [Fact]
    public void RemappingLeavesTheSelectionInTheFileItWasMadeIn()
    {
        var selection = new DiffSelectionModel();
        selection.SetRange("a.cs", At(1, 0), At(3, 4));

        selection.Remap(p => At(p.Row.Value + 1, p.Char.Value));

        Assert.Equal("a.cs", selection.Scope);
    }

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

    private DiffRowSet FullFile(FoldState? folds) => DiffRowSet.Build(
        new DiffRenderState.FullFile(
            Path,
            Source,
            AddedLineNumbers: new HashSet<int>(),
            Side: DiffSide.WorkingTree,
            Truncated: false,
            Emphasis: null,
            Annotations: new DiffAnnotations(null, fixture.Outline(string.Join('\n', Source)), null)),
        Loc(),
        folds);

    private static FoldState Collapsed(DiffRowSet open) => FoldState.Open(Path)
        .Toggled(open.Rows.OfType<DiffRow.Line>().Last(r => r.Fold is { Chevron: true }).Fold!.Value.Id);

    private static RowIndex RowOf(DiffRowSet set, FileLine line) =>
        set.RowForNewLine(line) ?? throw new InvalidOperationException($"line {line.Value} has no row");

    /// <summary>The mapping the views build: the position's anchor named against the outgoing
    /// stream, resolved in the rebuilt one.</summary>
    private static DiffTextPos? Mapped(IDiffRowSource before, IDiffRowSource after, DiffTextPos pos)
    {
        var anchor = before.AnchorAt(pos.Row);
        return anchor is { } named && after.RowAt(named) is { } row ? new DiffTextPos(row, pos.Char) : null;
    }

    private static DiffTextPos At(int row, int column) =>
        new(new RowIndex(row), new ExpandedColumn(column));

    private static DiffRowAnchor Anchor(DiffRowKey key) => new(key, 0);

    private static ILocalizationService Loc() => new LocalizationService(new State<Locale>(Locale.En));

    private static DiffResult TwoHunks() => new(
        RepoId: Guid.Empty,
        Path: "file.cs",
        OldPath: null,
        Side: DiffSide.Unstaged,
        IsBinary: false,
        IsModeOnly: false,
        OldMode: null,
        NewMode: null,
        Hunks:
        [
            new DiffHunk(10, 3, 10, 3, null,
            [
                new DiffLine(DiffLineKind.Context, 10, 10, "var alpha = 1;"),
                new DiffLine(DiffLineKind.Removed, 11, null, "var beta = 2;"),
                new DiffLine(DiffLineKind.Added, null, 11, "var gamma = 3;"),
                new DiffLine(DiffLineKind.Context, 12, 12, "return alpha;"),
            ]),
            new DiffHunk(16, 3, 16, 3, null,
            [
                new DiffLine(DiffLineKind.Context, 16, 16, "int x = 0;"),
                new DiffLine(DiffLineKind.Removed, 17, null, "old();"),
                new DiffLine(DiffLineKind.Added, null, 17, "fresh();"),
                new DiffLine(DiffLineKind.Context, 18, 18, "return x;"),
            ]),
        ],
        Truncated: false,
        ErrorMessage: null);
}
