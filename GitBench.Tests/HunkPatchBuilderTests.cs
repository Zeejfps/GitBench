using Xunit;

namespace GitBench.Tests;

// The "\ No newline at end of file" marker must survive a diff → patch round-trip. Dropping it
// makes git apply add a trailing newline the user never touched, corrupting the working tree on
// stage/discard.
public class HunkPatchBuilderTests
{
    private static DiffResult Diff(params DiffLine[] lines)
        => DiffFor("file.txt", truncated: false, lines);

    private static DiffResult DiffFor(string path, bool truncated, params DiffLine[] lines)
    {
        var hunk = new DiffHunk(1, CountOld(lines), 1, CountNew(lines), null, lines);
        return Result(path, truncated, hunk);
    }

    private static DiffResult Result(string path, bool truncated, params DiffHunk[] hunks)
        => new DiffResult(
            RepoId: Guid.Empty,
            Path: path,
            OldPath: null,
            Side: DiffSide.Unstaged,
            IsBinary: false,
            IsModeOnly: false,
            OldMode: null,
            NewMode: null,
            Hunks: hunks,
            Truncated: truncated,
            ErrorMessage: null);

    private static DiffLine Ctx(int n, string text) => new(DiffLineKind.Context, n, n, text);
    private static DiffLine Rem(int n, string text) => new(DiffLineKind.Removed, n, null, text);
    private static DiffLine Add(int n, string text) => new(DiffLineKind.Added, null, n, text);

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

    // #2 — a truncated diff keeps full @@ counts but a short body, so it must not be patchable.
    [Fact]
    public void TruncatedDiffIsNotPatchable()
    {
        var diff = DiffFor("file.txt", truncated: true, Rem(1, "foo"), Add(1, "bar"));
        Assert.False(HunkPatchBuilder.CanPatchHunk(diff));
    }

    [Fact]
    public void NonTruncatedDiffIsPatchable()
    {
        var diff = DiffFor("file.txt", truncated: false, Rem(1, "foo"), Add(1, "bar"));
        Assert.True(HunkPatchBuilder.CanPatchHunk(diff));
    }

    // #3 — a space in the path is left unquoted but the ---/+++ lines gain a trailing tab, and
    // the `diff --git` line carries no tab. This is exactly what `git diff` emits.
    [Fact]
    public void SpacedPathGetsTrailingTabOnFileLinesOnly()
    {
        var diff = DiffFor("my file.txt", truncated: false, Rem(1, "foo"), Add(1, "bar"));

        var patch = HunkPatchBuilder.Build(diff, 0);

        Assert.Contains("diff --git a/my file.txt b/my file.txt\n", patch);
        Assert.Contains("--- a/my file.txt\t\n", patch);
        Assert.Contains("+++ b/my file.txt\t\n", patch);
    }

    // #3 — a non-ASCII path is C-quoted with octal-escaped UTF-8 bytes on every header line and
    // carries no trailing tab. `git diff` emits "a/caf\303\251.txt" for café.txt (é = 0xC3 0xA9).
    [Fact]
    public void NonAsciiPathIsCQuoted()
    {
        var diff = DiffFor("café.txt", truncated: false, Rem(1, "foo"), Add(1, "bar"));

        var patch = HunkPatchBuilder.Build(diff, 0);

        Assert.Contains("diff --git \"a/caf\\303\\251.txt\" \"b/caf\\303\\251.txt\"\n", patch);
        Assert.Contains("--- \"a/caf\\303\\251.txt\"\n", patch);
        Assert.Contains("+++ \"b/caf\\303\\251.txt\"\n", patch);
    }

    // #4 — a coincidental -0,0 range on one hunk of a multi-hunk diff must not be mistaken for a
    // new file (no "new file mode", no /dev/null).
    [Fact]
    public void MultiHunkDiffDoesNotInferNewFileFromRange()
    {
        var insertAtTop = new DiffHunk(0, 0, 1, 1, null, new[] { Add(1, "added") });
        var laterEdit = new DiffHunk(5, 1, 6, 1, null, new[] { Rem(5, "old"), Add(6, "new") });
        var diff = Result("file.txt", truncated: false, insertAtTop, laterEdit);

        var patch = HunkPatchBuilder.Build(diff, 0);

        Assert.DoesNotContain("new file mode", patch);
        Assert.DoesNotContain("/dev/null", patch);
        Assert.Contains("--- a/file.txt\n", patch);
    }

    // #4 — a genuine new file (single hunk, -0,0) still gets the new-file header.
    [Fact]
    public void SingleHunkNewFileStillInferred()
    {
        var hunk = new DiffHunk(0, 0, 1, 1, null, new[] { Add(1, "added") });
        var diff = Result("file.txt", truncated: false, hunk);

        var patch = HunkPatchBuilder.Build(diff, 0);

        Assert.Contains("new file mode", patch);
        Assert.Contains("--- /dev/null\n", patch);
    }
}
