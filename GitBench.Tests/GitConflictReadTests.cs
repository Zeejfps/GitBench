using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// The two conflict reads the assistant's tools need from IGitService, against a repository git
// really did leave half-merged: the whole unmerged list in one answer, and one path's three merge
// stages as text. Pinned here rather than only through the tools because they are a public seam a
// conflict view would use too, and because what they have to get right — which stages exist, what a
// path git would quote comes back as — is git's shape, not JSON's.
public sealed class GitConflictReadTests : IDisposable
{
    private readonly ConflictedRepo _repo = ConflictedRepo.Merging();
    private readonly GitService _git = new(new RepoActivityTracker());

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void GetConflictedPaths_ListsEveryUnmergedPathAndNothingElse()
    {
        var conflicts = _git.GetConflictedPaths(_repo.Repo);

        Assert.Equal(
            new[] { "a.txt", "fresh.txt", "gone.txt", "logo.bin", "long.txt", "naïve name.txt" },
            conflicts.Select(c => c.Path));
    }

    // Stage 1 present means both sides started from a common blob; a side whose own stage is missing
    // is the side that deleted the file. This is the whole derivation the model is handed.
    [Theory]
    [InlineData("a.txt", ConflictChangeKind.Modified, ConflictChangeKind.Modified)]
    [InlineData("fresh.txt", ConflictChangeKind.Added, ConflictChangeKind.Added)]
    [InlineData("gone.txt", ConflictChangeKind.Modified, ConflictChangeKind.Deleted)]
    public void GetConflictedPaths_DerivesWhatEachSideDidFromTheStagesGitLeft(
        string path, ConflictChangeKind ours, ConflictChangeKind theirs)
    {
        var conflict = Assert.Single(_git.GetConflictedPaths(_repo.Repo), c => c.Path == path);

        Assert.Equal(ours, conflict.Ours);
        Assert.Equal(theirs, conflict.Theirs);
    }

    // `git ls-files -u` C-quotes a path like this one unless it is asked not to, and a quoted path
    // is not a path any other call will accept.
    [Fact]
    public void GetConflictedPaths_ReturnsANonAsciiPathAsSomethingTheOtherReadsAccept()
    {
        var conflict = Assert.Single(_git.GetConflictedPaths(_repo.Repo), c => c.Path.Contains("name"));

        Assert.Equal("naïve name.txt", conflict.Path);
        Assert.NotNull(_git.GetConflictStages(_repo.Repo, conflict.Path));
    }

    [Fact]
    public void GetConflictedPaths_OnARepositoryWithNothingUnmerged_IsEmpty()
    {
        _repo.Git("merge", "--abort");

        Assert.Empty(_git.GetConflictedPaths(_repo.Repo));
    }

    [Fact]
    public void GetConflictedPaths_AfterOnePathIsResolved_NoLongerLandsOnIt()
    {
        Assert.IsType<GitOutcome.Success>(_git.TakeOurs(_repo.Repo, "a.txt"));

        Assert.DoesNotContain("a.txt", _git.GetConflictedPaths(_repo.Repo).Select(c => c.Path));
    }

    [Fact]
    public void GetConflictStages_ReturnsTheThreeSidesAsTheyStandInTheIndex()
    {
        var stages = _git.GetConflictStages(_repo.Repo, "a.txt");

        Assert.NotNull(stages);
        Assert.Equal("one\ntwo\nthree\n", stages!.Base);
        Assert.Equal("one\nfrom main\nthree\n", stages.Ours);
        Assert.Equal("one\nfrom feature\nthree\n", stages.Theirs);
    }

    // The delete/modify case: there is no theirs blob to show, and saying so is different from
    // saying the side is empty.
    [Fact]
    public void GetConflictStages_WhenOneSideDeletedTheFile_LeavesThatSideNull()
    {
        var stages = _git.GetConflictStages(_repo.Repo, "gone.txt");

        Assert.NotNull(stages);
        Assert.Equal("still here\n", stages!.Base);
        Assert.Equal("still here, and edited\n", stages.Ours);
        Assert.Null(stages.Theirs);
    }

    [Fact]
    public void GetConflictStages_OnAnAddAddConflict_HasNoBase()
    {
        var stages = _git.GetConflictStages(_repo.Repo, "fresh.txt");

        Assert.NotNull(stages);
        Assert.Null(stages!.Base);
        Assert.Equal("main's idea\n", stages.Ours);
        Assert.Equal("feature's idea\n", stages.Theirs);
    }

    // A tracked, unconflicted file has no stages at all — the caller's is-unmerged precondition.
    [Fact]
    public void GetConflictStages_OnAPathThatIsNotUnmerged_IsNull()
    {
        Assert.Null(_git.GetConflictStages(_repo.Repo, "quiet.txt"));
    }

    [Fact]
    public void GetConflictStages_OnAPathTheRepositoryDoesNotHave_IsNull()
    {
        Assert.Null(_git.GetConflictStages(_repo.Repo, "never/existed.txt"));
    }

    // A path git would read as an option if it reached the command line unguarded.
    [Fact]
    public void GetConflictStages_OnAPathThatLooksLikeAnOption_IsNullRatherThanAnArgumentError()
    {
        Assert.Null(_git.GetConflictStages(_repo.Repo, "--all"));
    }
}
