using GitBench.Features.CodeIntel;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Git;

using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The loader's contract, and the reason it exists: a render leaves here finished. Colors used to
/// arrive on a second pass, so a diff painted plain for as long as that pass took — folding them
/// into the load is what removed the flash, and these pin it down.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public class DiffPreviewLoaderTests(CodeIntelFixture fixture)
{
    private const string NewSource =
        """
        namespace Acme.Auth;

        class AuthService
        {
            public bool Login(string user)
            {
                return Check(user);
            }

            private bool Check(string user) => user.Length > 0;
        }
        """;

    private const string OldSource =
        """
        namespace Acme.Auth;

        class AuthService
        {
            public bool Login(string user)
            {
                return true;
            }
        }
        """;

    [Fact]
    public void ADiffArrivesAlreadyColoredAndOutlined()
    {
        var render = Load(DiffViewMode.Diff);

        var loaded = Assert.IsType<DiffRenderState.Loaded>(render);
        Assert.NotNull(loaded.Annotations);
        Assert.NotNull(loaded.Annotations.Highlight);
        Assert.Equal("AuthService.Login(string)", loaded.Annotations.HunkHeader(loaded.Result.Hunks[0]));
    }

    /// <summary>Both sides: the removed rows a diff shows are colored from the before-side text,
    /// which is a separate fetch and a separate parse from the after-side one.</summary>
    [Fact]
    public void BothSidesOfADiffAreColored()
    {
        var loaded = Assert.IsType<DiffRenderState.Loaded>(Load(DiffViewMode.Diff));
        var highlight = loaded.Annotations!.Highlight!;

        Assert.NotEmpty(highlight.ForLine(DiffLineKind.Added, null, 6));
        Assert.NotEmpty(highlight.ForLine(DiffLineKind.Removed, 6, null));
    }

    [Fact]
    public void AFullFileArrivesAlreadyColored()
    {
        var render = Load(DiffViewMode.FullFile);

        var fullFile = Assert.IsType<DiffRenderState.FullFile>(render);
        Assert.NotNull(fullFile.Annotations);
        Assert.NotNull(fullFile.Annotations.NewSide);
        Assert.NotEmpty(fullFile.Annotations.Highlight!.ForLine(DiffLineKind.Context, null, 5));
    }

    /// <summary>The full-file view draws the after-file alone, so the before side is never fetched
    /// for it — the saving that pays for annotating on the load pass instead of after it.</summary>
    [Fact]
    public void AFullFileNeverReadsTheBeforeSide()
    {
        var git = new StubDiffReader(NewSource, OldSource, DiffOf());

        Load(DiffViewMode.FullFile, git);

        Assert.Equal(0, git.OldSideReads);
        Assert.Null(Assert.IsType<DiffRenderState.FullFile>(Load(DiffViewMode.FullFile, git)).Annotations!.OldSide);
    }

    [Fact]
    public void AFileWithNoCurrentVersionSaysSoRatherThanRenderingEmpty()
    {
        var git = new StubDiffReader(newText: null, OldSource, DiffOf());

        var render = Load(DiffViewMode.FullFile, git);

        Assert.Equal("no current version", Assert.IsType<DiffRenderState.Placeholder>(render).Text);
    }

    private DiffRenderState Load(DiffViewMode mode, StubDiffReader? git = null)
    {
        var loader = new DiffPreviewLoader(
            git ?? new StubDiffReader(NewSource, OldSource, DiffOf()), new NoConflicts(), fixture.Extractor);
        return loader.Load(new DiffPreviewRequest(
            new Repo(Guid.NewGuid(), "/repo", "repo"),
            new DiffTarget("AuthService.cs", DiffSide.Unstaged),
            mode,
            Preview: false,
            BinaryText: "binary",
            NoCurrentVersionText: "no current version"));
    }

    // One hunk that both removes (old line 6) and adds (new line 6), so each side has a row to color.
    private static DiffResult DiffOf() => new(
        RepoId: Guid.Empty,
        Path: "AuthService.cs",
        OldPath: null,
        Side: DiffSide.Unstaged,
        IsBinary: false,
        IsModeOnly: false,
        OldMode: null,
        NewMode: null,
        Hunks: [new DiffHunk(5, 3, 5, 3, null, [
            new DiffLine(DiffLineKind.Context, 5, 5, "    {"),
            new DiffLine(DiffLineKind.Removed, 6, null, "        return true;"),
            new DiffLine(DiffLineKind.Added, null, 6, "        return Check(user);"),
            new DiffLine(DiffLineKind.Context, 7, 7, "    }"),
        ])],
        Truncated: false,
        ErrorMessage: null);

    private sealed class StubDiffReader(string? newText, string oldText, DiffResult diff) : IGitDiffReader
    {
        public int OldSideReads { get; private set; }

        public Fetched<IReadOnlyList<FileChange>> LoadRangeFiles(Repo repo, string baseSha, string headSha)
            => throw new NotSupportedException();

        public DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null, string? baseSha = null)
            => diff;

        public string? GetFileText(Repo repo, string path, DiffSide side, bool oldSide, string? commitSha = null, string? baseSha = null)
        {
            if (!oldSide) return newText;
            OldSideReads++;
            return oldText;
        }

        public byte[]? GetFileBytes(Repo repo, string path, DiffSide side, bool oldSide, int maxBytes, string? commitSha = null, string? baseSha = null)
            => null;
    }

    private sealed class NoConflicts : IGitConflictOperations
    {
        public ConflictContext? GetConflictContext(Repo repo, string path) => null;

        public GitOutcome TakeOurs(Repo repo, string path) => throw new NotSupportedException();

        public GitOutcome TakeTheirs(Repo repo, string path) => throw new NotSupportedException();

        public GitOutcome TakeBoth(Repo repo, string path) => throw new NotSupportedException();

        public GitOutcome MarkResolved(Repo repo, string path) => throw new NotSupportedException();

        public IReadOnlyList<ConflictedPath> GetConflictedPaths(Repo repo) => throw new NotSupportedException();

        public ConflictStages? GetConflictStages(Repo repo, string path) => throw new NotSupportedException();
    }
}
