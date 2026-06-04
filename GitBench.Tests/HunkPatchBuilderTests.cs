using GitGui;
using Xunit;

namespace GitBench.Tests;

// The "\ No newline at end of file" marker must survive a diff → patch round-trip. Dropping it
// makes git apply add a trailing newline the user never touched, corrupting the working tree on
// stage/discard.
public class HunkPatchBuilderTests
{
    private static DiffResult Diff(params DiffLine[] lines)
    {
        var hunk = new DiffHunk(1, CountOld(lines), 1, CountNew(lines), null, lines);
        return new DiffResult(
            RepoId: Guid.Empty,
            Path: "file.txt",
            OldPath: null,
            Side: DiffSide.Unstaged,
            IsBinary: false,
            IsModeOnly: false,
            OldMode: null,
            NewMode: null,
            Hunks: new[] { hunk },
            Truncated: false,
            ErrorMessage: null);
    }

    private static int CountOld(DiffLine[] lines)
        => lines.Count(l => l.Kind != DiffLineKind.Added);

    private static int CountNew(DiffLine[] lines)
        => lines.Count(l => l.Kind != DiffLineKind.Removed);

    [Fact]
    public void EmitsNoNewlineMarkerAfterTheFlaggedLine()
    {
        var diff = Diff(
            new DiffLine(DiffLineKind.Removed, 1, null, "foo") { NoNewlineAtEof = true },
            new DiffLine(DiffLineKind.Added, null, 1, "bar") { NoNewlineAtEof = true });

        var patch = HunkPatchBuilder.Build(diff, 0);

        Assert.Contains("-foo\n\\ No newline at end of file\n", patch);
        Assert.Contains("+bar\n\\ No newline at end of file\n", patch);
    }

    [Fact]
    public void OnlyTheFlaggedSideGetsTheMarker()
    {
        // Old side had no trailing newline, new side does: only the removed line carries it.
        var diff = Diff(
            new DiffLine(DiffLineKind.Removed, 1, null, "foo") { NoNewlineAtEof = true },
            new DiffLine(DiffLineKind.Added, null, 1, "bar"));

        var patch = HunkPatchBuilder.Build(diff, 0);

        Assert.Contains("-foo\n\\ No newline at end of file\n+bar\n", patch);
        Assert.DoesNotContain("+bar\n\\ No newline", patch);
    }

    [Fact]
    public void OrdinaryLinesEmitNoMarker()
    {
        var diff = Diff(
            new DiffLine(DiffLineKind.Context, 1, 1, "ctx"),
            new DiffLine(DiffLineKind.Removed, 2, null, "foo"),
            new DiffLine(DiffLineKind.Added, null, 2, "bar"));

        var patch = HunkPatchBuilder.Build(diff, 0);

        Assert.DoesNotContain("No newline", patch);
    }
}
