using GitBench.Features.Diff.Reading;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// The compiler is the seam a model plans against. Its numbering has to stay put (a plan is cached
// against it), and every rejection here is a way an abridged diff could otherwise mislead: rows
// hidden twice, folds spanning hunks or mixing polarity, elisions that invent text.
public class ReadingPlanCompilerTests
{
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

    private static DiffLine Ctx(string text) => new(DiffLineKind.Context, 1, 1, text);
    private static DiffLine Add(string text) => new(DiffLineKind.Added, null, 1, text);
    private static DiffLine Rem(string text) => new(DiffLineKind.Removed, 1, null, text);

    private static ReadingRowIndex Index(params DiffResult[] files) => ReadingRowIndex.Build(files);

    private static ReadingPlan Plan(
        IReadOnlyList<ReadingRemoval>? remove = null,
        IReadOnlyList<ReadingFold>? fold = null,
        IReadOnlyList<ReadingElision>? replace = null)
        => new(remove ?? [], replace ?? [], fold ?? [], "summary");

    [Fact]
    public void NumbersEveryHunkLineAcrossFilesInOrder()
    {
        var index = Index(
            File("a.txt", Hunk(Ctx("one"), Add("two"))),
            File("b.txt", Hunk(Rem("three"))));

        Assert.Equal(3, index.Count);
        Assert.Equal("one", index.Line(1).Text);
        Assert.Equal("two", index.Line(2).Text);
        Assert.Equal("three", index.Line(3).Text);
        Assert.Equal(new ReadingRowRef(1, 0, 0), index.Locate(3));
        Assert.Equal(2, index.OrdinalOf(0, 0, 1));
    }

    // The numbered rendering is what the model addresses rows by, so the gutter and the diff
    // marker must both be present and must not be part of the line's source text.
    [Fact]
    public void RendersANumberedGutterWithMarkersAndHeadings()
    {
        var index = Index(File("a.txt", Hunk(Ctx("keep"), Add("new"), Rem("old"))));

        Assert.Equal(
            "=== a.txt\n@@ -1,3 +1,3 @@\n1| keep\n2|+new\n3|-old\n",
            index.Render());
    }

    [Fact]
    public void HidesRemovedRangesAndCountsRetention()
    {
        var index = Index(File("a.txt", Hunk(Add("one"), Add("two"), Add("three"), Add("four"))));

        var compiled = ReadingPlanCompiler.Compile(index, Plan(remove: [new ReadingRemoval(2, 3)]));

        Assert.True(compiled.Succeeded);
        var overlay = compiled.Overlay!;
        Assert.False(overlay.IsHidden(1));
        Assert.True(overlay.IsHidden(2));
        Assert.True(overlay.IsHidden(3));
        Assert.False(overlay.IsHidden(4));
        Assert.Equal(4, overlay.Stats.RawChanged);
        Assert.Equal(2, overlay.Stats.VisibleChanged);
        Assert.Equal(50, overlay.Stats.RetainedPercent);
    }

    [Fact]
    public void RejectsOverlappingRemovals()
    {
        var index = Index(File("a.txt", Hunk(Add("one"), Add("two"), Add("three"))));

        var compiled = ReadingPlanCompiler.Compile(
            index,
            Plan(remove: [new ReadingRemoval(1, 2), new ReadingRemoval(2, 3)]));

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Problems, p => p.Contains("overlaps"));
    }

    [Fact]
    public void RejectsRowsOutsideTheNumbering()
    {
        var index = Index(File("a.txt", Hunk(Add("one"))));

        var compiled = ReadingPlanCompiler.Compile(index, Plan(remove: [new ReadingRemoval(1, 9)]));

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Problems, p => p.Contains("rows 1-1"));
    }

    // A fold emits one generated row standing in for the range, so the range has to belong to one
    // hunk and one side — otherwise the ellipsis would claim a shape the diff does not have.
    [Fact]
    public void FoldsARunAndDerivesItsMarkerAndIndent()
    {
        var index = Index(File("a.txt", Hunk(
            Add("func f() {"),
            Add("        a := 1"),
            Add("    b := 2"),
            Add("        c := 3"),
            Add("}"))));

        var compiled = ReadingPlanCompiler.Compile(index, Plan(fold: [new ReadingFold(2, 4)]));

        Assert.True(compiled.Succeeded);
        var fold = compiled.Overlay!.FoldAt(2);
        Assert.NotNull(fold);
        Assert.Equal(DiffLineKind.Added, fold!.Kind);
        Assert.Equal("    ", fold.Indent);
        Assert.Equal(3, fold.HiddenCount);
        Assert.True(compiled.Overlay.IsHidden(3));
        Assert.Null(compiled.Overlay.FoldAt(3));
    }

    [Fact]
    public void RejectsAFoldThatMixesAddedAndRemovedRows()
    {
        var index = Index(File("a.txt", Hunk(Add("one"), Rem("two"))));

        var compiled = ReadingPlanCompiler.Compile(index, Plan(fold: [new ReadingFold(1, 2)]));

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Problems, p => p.Contains("mix"));
    }

    [Fact]
    public void RejectsAFoldSpanningTwoHunks()
    {
        var index = Index(File("a.txt", Hunk(Add("one")), Hunk(Add("two"))));

        var compiled = ReadingPlanCompiler.Compile(index, Plan(fold: [new ReadingFold(1, 2)]));

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Problems, p => p.Contains("more than one hunk"));
    }

    [Fact]
    public void RejectsASingleRowFold()
    {
        var index = Index(File("a.txt", Hunk(Add("one"), Add("two"))));

        var compiled = ReadingPlanCompiler.Compile(index, Plan(fold: [new ReadingFold(2, 2)]));

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Problems, p => p.Contains("two or more rows"));
    }

    [Fact]
    public void RejectsAFoldOverAnAlreadyRemovedRow()
    {
        var index = Index(File("a.txt", Hunk(Add("one"), Add("two"), Add("three"))));

        var compiled = ReadingPlanCompiler.Compile(
            index,
            Plan(remove: [new ReadingRemoval(2, 2)], fold: [new ReadingFold(2, 3)]));

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Problems, p => p.Contains("already removed"));
    }

    [Fact]
    public void AppliesAnElisionToTheRowText()
    {
        var index = Index(File("a.go", Hunk(Add("    t.Errorf(\"want %d, got %d\", a, b)"))));

        var compiled = ReadingPlanCompiler.Compile(
            index,
            Plan(replace: [new ReadingElision(1, "\"want %d, got %d\", a, b", "…")]));

        Assert.True(compiled.Succeeded);
        Assert.Equal("    t.Errorf(…)", compiled.Overlay!.ElidedText(1));
    }

    [Fact]
    public void RejectsAnElisionThatInventsText()
    {
        var index = Index(File("a.go", Hunk(Add("value := compute(x)"))));

        var compiled = ReadingPlanCompiler.Compile(
            index,
            Plan(replace: [new ReadingElision(1, "compute(x)", "compute(y)…")]));

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Problems, p => p.Contains("not an elision"));
    }

    [Fact]
    public void RejectsAnElisionOnAHiddenRow()
    {
        var index = Index(File("a.go", Hunk(Add("value := compute(x)"), Add("other"))));

        var compiled = ReadingPlanCompiler.Compile(
            index,
            Plan(remove: [new ReadingRemoval(1, 1)], replace: [new ReadingElision(1, "compute(x)", "compute(…)")]));

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Problems, p => p.Contains("already removed"));
    }

    // Imports go without being asked for, so a plan that spends no coordinates at all still
    // produces a reading diff.
    [Fact]
    public void HidesImportsWithNoPlanAtAll()
    {
        var index = Index(File("a.cs", Hunk(
            Rem("using System;"),
            Add("using System.Text;"),
            Ctx(""),
            Add("var x = 1;"))));

        var overlay = ReadingPlanCompiler.Mechanical(index);

        Assert.True(overlay.IsHidden(1));
        Assert.True(overlay.IsHidden(2));
        Assert.False(overlay.IsHidden(4));
        Assert.Equal(3, overlay.Stats.RawChanged);
        Assert.Equal(1, overlay.Stats.VisibleChanged);
    }

    // A `using` statement shares the keyword with the directive and is ordinary control flow.
    [Fact]
    public void KeepsUsingStatementsAndDeclarations()
    {
        var index = Index(File("a.cs", Hunk(
            Add("using (var stream = Open())"),
            Add("using var reader = new StreamReader(stream);"))));

        var overlay = ReadingPlanCompiler.Mechanical(index);

        Assert.False(overlay.IsHidden(1));
        Assert.False(overlay.IsHidden(2));
    }

    [Fact]
    public void HidesGroupedGoImportsIncludingTheirFraming()
    {
        var index = Index(File("a.go", Hunk(
            Ctx("import ("),
            Ctx("\t\"fmt\""),
            Rem("\t\"math/rand\""),
            Add("\tcrand \"crypto/rand\""),
            Ctx(")"),
            Ctx(""),
            Add("n := crand.Int()"))));

        var overlay = ReadingPlanCompiler.Mechanical(index);

        for (var row = 1; row <= 5; row++)
            Assert.True(overlay.IsHidden(row), $"row {row} should be import scaffolding");
        Assert.False(overlay.IsHidden(7));
    }

    [Fact]
    public void HidesPythonImportsIncludingParenthesisedLists()
    {
        var index = Index(File("a.py", Hunk(
            Add("import os"),
            Add("from x import ("),
            Add("    a,"),
            Add("    b,"),
            Add(")"),
            Add("value = a(os.sep)"))));

        var overlay = ReadingPlanCompiler.Mechanical(index);

        for (var row = 1; row <= 5; row++)
            Assert.True(overlay.IsHidden(row), $"row {row} should be import scaffolding");
        Assert.False(overlay.IsHidden(6));
    }

    [Fact]
    public void RejectsAFoldCrossingImportsIntoBehaviouralRows()
    {
        var index = Index(File("a.cs", Hunk(
            Add("using System;"),
            Add("var x = 1;"),
            Add("var y = 2;"))));

        var compiled = ReadingPlanCompiler.Compile(index, Plan(fold: [new ReadingFold(1, 3)]));

        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Problems, p => p.Contains("import rows"));
    }

    // An import-only fold is redundant rather than wrong: the rows are already gone, and no
    // ellipsis should stand in for scaffolding the reader never asked to see.
    [Fact]
    public void IgnoresAnImportOnlyFold()
    {
        var index = Index(File("a.cs", Hunk(
            Add("using System;"),
            Add("using System.Text;"),
            Add("var x = 1;"))));

        var compiled = ReadingPlanCompiler.Compile(index, Plan(fold: [new ReadingFold(1, 2)]));

        Assert.True(compiled.Succeeded);
        Assert.Null(compiled.Overlay!.FoldAt(1));
        Assert.True(compiled.Overlay.IsHidden(1));
    }

    [Fact]
    public void ReportsAFileWhoseChangedRowsAreAllHidden()
    {
        var files = new[]
        {
            File("gen.cs", Hunk(Add("generated one"), Add("generated two"))),
            File("real.cs", Hunk(Add("kept"))),
        };
        var index = Index(files);

        var compiled = ReadingPlanCompiler.Compile(index, Plan(remove: [new ReadingRemoval(1, 2)]));

        Assert.True(compiled.Succeeded);
        Assert.True(compiled.Overlay!.FileIsFullyHidden(0));
        Assert.False(compiled.Overlay.FileIsFullyHidden(1));
        Assert.Equal(1, compiled.Overlay.Stats.VisibleFiles);
        Assert.Equal(2, compiled.Overlay.Stats.RawFiles);
    }

    [Fact]
    public void NarrowsToOneFileForTheRowFlattener()
    {
        var files = new[]
        {
            File("a.cs", Hunk(Add("one"))),
            File("b.cs", Hunk(Add("two"), Add("three"))),
        };
        var index = Index(files);

        var overlay = ReadingPlanCompiler.Compile(index, Plan(remove: [new ReadingRemoval(2, 3)])).Overlay!;
        var perFile = overlay.ForFile(files[1]);

        Assert.NotNull(perFile);
        Assert.True(perFile!.Value.IsHidden(0, 0));
        Assert.True(perFile.Value.IsHidden(0, 1));
        Assert.False(perFile.Value.HunkHasVisibleRows(files[1], 0));
        Assert.True(overlay.ForFile(files[0])!.Value.HunkHasVisibleRows(files[0], 0));
    }
}
