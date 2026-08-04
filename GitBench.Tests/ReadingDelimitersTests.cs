using GitBench.Features.Diff.Reading;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// An abridged diff that drops a closing brace reads as a method that never ends, with whatever
// follows apparently nested inside it. The reader then reconstructs braces instead of reading the
// change, which is the opposite of the point. The compiler gives those rows back rather than
// rejecting the plan: there is no decision here for a model to make.
public class ReadingDelimitersTests
{
    private static DiffResult File(string path, params DiffLine[] lines)
        => new(
            RepoId: Guid.Empty,
            Path: path,
            OldPath: null,
            Side: DiffSide.Commit,
            IsBinary: false,
            IsModeOnly: false,
            OldMode: null,
            NewMode: null,
            Hunks: [new DiffHunk(1, lines.Length, 1, lines.Length, null, lines)],
            Truncated: false,
            ErrorMessage: null);

    private static DiffLine Add(string text) => new(DiffLineKind.Added, null, 1, text);

    private static ReadingOverlay Compile(DiffResult file, ReadingPlan plan)
    {
        var compiled = ReadingPlanCompiler.Compile(ReadingRowIndex.Build([file]), plan);
        Assert.True(compiled.Succeeded, string.Join("; ", compiled.Problems));
        return compiled.Overlay!;
    }

    // The shape that showed up in a real run: the closing brace of one method removed, so the next
    // method's signature appeared to sit inside it.
    [Fact]
    public void GivesBackAClosingBraceWhoseMethodIsStillVisible()
    {
        var file = File("a.cs",
            Add("public void SetReading(ReadingOverlay? overlay)"),
            Add("{"),
            Add("    Update(s => s);"),
            Add("}"),
            Add(""),
            Add("public void ExpandFold(IReadOnlyList<int> startRows)"));

        var overlay = Compile(file, new ReadingPlan([new ReadingRemoval(4, 5)], [], []));

        Assert.False(overlay.IsHidden(4));
        Assert.True(overlay.IsHidden(5));
    }

    // A block hidden in full takes its closer with it: nothing is left on screen for it to close.
    [Fact]
    public void LetsAWhollyHiddenBlockKeepItsBraces()
    {
        var file = File("a.cs",
            Add("public void Kept()"),
            Add("{"),
            Add("    if (x)"),
            Add("    {"),
            Add("        Work();"),
            Add("    }"),
            Add("}"));

        var overlay = Compile(file, new ReadingPlan([new ReadingRemoval(3, 6)], [], []));

        for (var row = 3; row <= 6; row++)
            Assert.True(overlay.IsHidden(row), $"row {row} should stay hidden");
        Assert.False(overlay.IsHidden(7));
    }

    // A fold that swallowed the closing row is pulled back off it rather than dropped entirely, so
    // the body still collapses and the brace still shows.
    [Fact]
    public void ShrinksAFoldOffTheClosingRowItSwallowed()
    {
        var file = File("a.cs",
            Add("void Run()"),
            Add("{"),
            Add("    a();"),
            Add("    b();"),
            Add("    c();"),
            Add("}"));

        var overlay = Compile(file, new ReadingPlan([], [], [new ReadingFold(3, 6)]));

        var fold = overlay.FoldAt(3);
        Assert.NotNull(fold);
        Assert.Equal(5, fold!.EndRow);
        Assert.Equal(3, fold.HiddenCount);
        Assert.False(overlay.IsHidden(6));
    }

    // Shrinking a two-row fold leaves one row, which is not worth an ellipsis: both come back.
    [Fact]
    public void DropsAFoldLeftWithNothingWorthStandingIn()
    {
        var file = File("a.cs",
            Add("void Run()"),
            Add("{"),
            Add("    a();"),
            Add("}"));

        var overlay = Compile(file, new ReadingPlan([], [], [new ReadingFold(3, 4)]));

        Assert.Null(overlay.FoldAt(3));
        Assert.False(overlay.IsHidden(3));
        Assert.False(overlay.IsHidden(4));
    }

    // Python closes blocks by indentation, so brace matching has nothing to say about it.
    [Fact]
    public void LeavesIndentationLanguagesAlone()
    {
        var file = File("a.py",
            Add("def run():"),
            Add("    work()"),
            Add("    done()"));

        var overlay = Compile(file, new ReadingPlan([new ReadingRemoval(3, 3)], [], []));

        Assert.True(overlay.IsHidden(3));
    }
}
