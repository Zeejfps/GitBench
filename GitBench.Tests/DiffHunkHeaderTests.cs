using GitBench.Features.CodeIntel;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// What a hunk separator says it is. The header is derived at flatten time from the outlines the
// annotations carry, never stored per hunk index — ApplyOptimisticHunkRemoval drops a hunk out of
// the middle of the list, which would silently shift every index-keyed label after it.
[Collection(nameof(CodeIntelCollection))]
public class DiffHunkHeaderTests(CodeIntelFixture fixture)
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

            public bool Login(string user, int attempt)
            {
                return Check(user) && attempt > 0;
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

            private bool Legacy()
            {
                return false;
            }
        }
        """;

    [Fact]
    public void TheHeaderNamesTheEnclosingDeclarationInAFileScopedNamespaceFile()
    {
        var diff = DiffOf(Hunk(6, 3, 6, 3, "@@ git guess", Ctx(6, 6, "    {"), Add(7, "        return Check(user);"), Ctx(7, 8, "    }")));

        Assert.Equal(["AuthService.Login(string)"], Headers(diff, NewSideAnnotations()));
    }

    [Fact]
    public void OverloadsGetDistinctHeaders()
    {
        var diff = DiffOf(
            Hunk(6, 3, 6, 3, null, Ctx(6, 6, "    {"), Add(7, "        return Check(user);"), Ctx(7, 8, "    }")),
            Hunk(10, 3, 11, 3, null, Ctx(11, 11, "    {"), Add(12, "        return Check(user) && attempt > 0;"), Ctx(12, 13, "    }")));

        Assert.Equal(
            ["AuthService.Login(string)", "AuthService.Login(string, int)"],
            Headers(diff, NewSideAnnotations()));
    }

    [Fact]
    public void APureDeletionNamesTheDeclarationItWasRemovedFrom()
    {
        var diff = DiffOf(Hunk(
            9, 6, 9, 2, "@@ git guess",
            Ctx(9, 9, string.Empty),
            Rem(10, "    private bool Legacy()"),
            Rem(11, "    {"),
            Rem(12, "        return false;"),
            Rem(13, "    }"),
            Ctx(14, 10, "}")));

        var annotations = new DiffAnnotations(null, fixture.Outline(NewSource), fixture.Outline(OldSource));

        Assert.Equal(["AuthService.Legacy()"], Headers(diff, annotations));
    }

    [Fact]
    public void AHunkCarryingNoLinesKeepsGitsOwnHeader()
    {
        // DiffOptions.TruncationLineCap drops a late hunk's lines while keeping its @@ counts.
        var diff = DiffOf(Hunk(40, 3, 40, 3, "@@ git guess"));

        Assert.Equal(["@@ git guess"], Headers(diff, NewSideAnnotations()));
    }

    [Fact]
    public void AHunkInsideNoDeclarationKeepsGitsOwnHeader()
    {
        var diff = DiffOf(Hunk(1, 2, 1, 3, "@@ git guess", Ctx(1, 1, "namespace Acme.Auth;"), Add(2, string.Empty), Ctx(2, 3, "class AuthService")));

        Assert.Equal(["@@ git guess"], Headers(diff, NewSideAnnotations()));
    }

    [Fact]
    public void ADeclarationPathThatIsOnlyANamespaceKeepsGitsOwnHeader()
    {
        var diff = DiffOf(Hunk(1, 1, 1, 1, "@@ git guess", Add(1, "namespace Acme.Auth;")));

        Assert.Equal(["@@ git guess"], Headers(diff, NewSideAnnotations()));
    }

    [Fact]
    public void WithoutAnnotationsEverySeparatorIsExactlyWhatGitReported()
    {
        var diff = DiffOf(
            Hunk(6, 3, 6, 3, "@@ git guess", Ctx(6, 6, "    {"), Add(7, "        return Check(user);"), Ctx(7, 8, "    }")),
            Hunk(10, 3, 11, 3, null, Ctx(11, 11, "    {"), Add(12, "        x"), Ctx(12, 13, "    }")));

        Assert.Equal(["@@ git guess", null], Headers(diff, annotations: null));
    }

    [Fact]
    public void HeadersStayWithTheirOwnHunkAcrossAnOptimisticRemoval()
    {
        var first = Hunk(6, 3, 6, 3, null, Ctx(6, 6, "    {"), Add(7, "        return Check(user);"), Ctx(7, 8, "    }"));
        var second = Hunk(10, 3, 11, 3, null, Ctx(11, 11, "    {"), Add(12, "        return Check(user) && attempt > 0;"), Ctx(12, 13, "    }"));
        var annotations = NewSideAnnotations();

        // What ApplyOptimisticHunkRemoval does: drop a hunk out of the middle and carry the same
        // annotations forward. Anything keyed by hunk index would relabel what is left.
        var afterStaging = DiffOf(second);

        Assert.Equal(["AuthService.Login(string)", "AuthService.Login(string, int)"], Headers(DiffOf(first, second), annotations));
        Assert.Equal(["AuthService.Login(string, int)"], Headers(afterStaging, annotations));
    }

    [Fact]
    public void TheStagePatchStillCarriesGitsHeaderNotTheDerivedOne()
    {
        var diff = DiffOf(Hunk(6, 3, 6, 3, "class AuthService", Ctx(6, 6, "    {"), Add(7, "        return Check(user);"), Ctx(7, 8, "    }")));

        Assert.Equal(["AuthService.Login(string)"], Headers(diff, NewSideAnnotations()));

        var patch = HunkPatchBuilder.Build(diff, 0);

        Assert.Contains("@@ -6,3 +6,3 @@ class AuthService\n", patch);
        Assert.DoesNotContain("AuthService.Login(string)", patch);
    }

    [Fact]
    public void ALanguageTreeSitterDoesNotKnowStillGetsItsColors()
    {
        var git = new OneFileDiffReader(NewSource, NewSource);
        var diff = DiffOf(Hunk(6, 3, 6, 3, null, Ctx(6, 6, "    {"), Add(7, "        x"), Ctx(7, 8, "    }"))) with { Path = "notes.md" };

        var annotations = DiffAnnotationCoordinator.Compute(fixture.Extractor, git, Repo(), diff, commitSha: null);

        Assert.NotNull(annotations);
        Assert.NotNull(annotations.Highlight);
        Assert.Null(annotations.NewSide);
        Assert.Null(annotations.OldSide);
    }

    [Fact]
    public void StructureDisabledProducesNoOutlines()
    {
        var git = new OneFileDiffReader(NewSource, OldSource);
        var diff = DiffOf(Hunk(6, 3, 6, 3, null, Ctx(6, 6, "    {"), Add(7, "        x"), Ctx(7, 8, "    }")));

        DiffOptions.StructureEnabled = false;
        try
        {
            Assert.Null(DiffAnnotationCoordinator.ComputeOutlines(fixture.Extractor, git, Repo(), diff, commitSha: null));

            var withColors = DiffAnnotationCoordinator.Compute(fixture.Extractor, git, Repo(), diff, commitSha: null);
            Assert.NotNull(withColors);
            Assert.Null(withColors.NewSide);
            Assert.Null(withColors.OldSide);
        }
        finally
        {
            DiffOptions.StructureEnabled = true;
        }
    }

    [Fact]
    public void OutlinesAloneSkipTheTokenizer()
    {
        var git = new OneFileDiffReader(NewSource, OldSource);
        var diff = DiffOf(Hunk(6, 3, 6, 3, null, Ctx(6, 6, "    {"), Add(7, "        return Check(user);"), Ctx(7, 8, "    }")));

        var annotations = DiffAnnotationCoordinator.ComputeOutlines(fixture.Extractor, git, Repo(), diff, commitSha: null);

        Assert.NotNull(annotations);
        Assert.Null(annotations.Highlight);
        Assert.Equal("AuthService.Login(string)", annotations.HunkHeader(diff.Hunks[0]));
    }

    [Fact]
    public void AHeaderTooWideForItsRowLosesItsOuterPathNotItsName()
    {
        var painter = new DiffRowPainter(new LocalizationService(new State<Locale>(Locale.En))) { MonoAdvance = 10f };

        Assert.Equal("AuthService.Login(string)", painter.FitHeader("AuthService.Login(string)", 400f));
        Assert.Equal("…n(string)", painter.FitHeader("AuthService.Login(string)", 100f));
    }

    private DiffAnnotations NewSideAnnotations() => new(null, fixture.Outline(NewSource), null);

    private static IReadOnlyList<string?> Headers(DiffResult diff, DiffAnnotations? annotations)
    {
        var loc = new LocalizationService(new State<Locale>(Locale.En));
        var rows = DiffRowSet.Build(new DiffRenderState.Loaded(diff, annotations), loc).Rows;
        return rows.OfType<DiffRow.HunkSeparator>().Where(s => s.Range.Length > 0).Select(s => s.Header).ToArray();
    }

    private static Repo Repo() => new(Guid.NewGuid(), "/repo", "repo");

    private static DiffResult DiffOf(params DiffHunk[] hunks) => new(
        RepoId: Guid.Empty,
        Path: "AuthService.cs",
        OldPath: null,
        Side: DiffSide.Unstaged,
        IsBinary: false,
        IsModeOnly: false,
        OldMode: null,
        NewMode: null,
        Hunks: hunks,
        Truncated: false,
        ErrorMessage: null);

    private static DiffHunk Hunk(int oldStart, int oldLines, int newStart, int newLines, string? header, params DiffLine[] lines)
        => new(oldStart, oldLines, newStart, newLines, header, lines);

    private static DiffLine Ctx(int oldLine, int newLine, string text) => new(DiffLineKind.Context, oldLine, newLine, text);
    private static DiffLine Rem(int oldLine, string text) => new(DiffLineKind.Removed, oldLine, null, text);
    private static DiffLine Add(int newLine, string text) => new(DiffLineKind.Added, null, newLine, text);

    // One file's two sides, so the coordinator's blob fetch has something to return. Every other
    // read is out of scope for these tests and says so rather than answering with a stand-in.
    private sealed class OneFileDiffReader(string newText, string oldText) : IGitDiffReader
    {
        public Fetched<IReadOnlyList<FileChange>> LoadRangeFiles(Repo repo, string baseSha, string headSha)
            => throw new NotSupportedException();

        public DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null, string? baseSha = null)
            => throw new NotSupportedException();

        public string? GetFileText(Repo repo, string path, DiffSide side, bool oldSide, string? commitSha = null, string? baseSha = null)
            => oldSide ? oldText : newText;

        public byte[]? GetFileBytes(Repo repo, string path, DiffSide side, bool oldSide, int maxBytes, string? commitSha = null, string? baseSha = null)
            => throw new NotSupportedException();
    }
}
