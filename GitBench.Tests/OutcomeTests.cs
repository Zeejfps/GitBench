using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// The outcome hierarchies are the contract between GitService and every view model: a
// non-failure case must never surface a FailureMessage (generic infrastructure like
// AsyncCommand.ForOutcome treats null as success), and Fetched.Map must preserve the
// failure's Detail so the local-changes error dialog keeps its full git output.
public class OutcomeTests
{
    [Fact]
    public void GitOutcome_FailureMessage_NullOnSuccess()
    {
        Assert.Null(GitOutcome.Ok.FailureMessage);
        Assert.Equal("boom", new GitOutcome.Failed("boom").FailureMessage);
    }

    [Fact]
    public void MergeLikeOutcome_Conflicted_IsNotAFailure()
    {
        Assert.Null(MergeLikeOutcome.Ok.FailureMessage);
        Assert.Null(new MergeLikeOutcome.Conflicted().FailureMessage);
        Assert.Equal("boom", new MergeLikeOutcome.Failed("boom").FailureMessage);
    }

    [Fact]
    public void PullOutcome_Diverged_IsNotAFailure()
    {
        Assert.Null(new PullOutcome.Diverged().FailureMessage);
        Assert.Equal("boom", new PullOutcome.Failed("boom").FailureMessage);
    }

    [Fact]
    public void ContinueOutcome_MoreConflicts_SurfacesItsMessage()
    {
        Assert.Null(ContinueOutcome.Ok.FailureMessage);
        Assert.Equal("unmerged", new ContinueOutcome.MoreConflicts("unmerged").FailureMessage);
        Assert.Equal("boom", new ContinueOutcome.Failed("boom").FailureMessage);
    }

    [Fact]
    public void AbortOutcome_Failed_CarriesForceQuitAvailability()
    {
        var failed = new AbortOutcome.Failed("stuck", ForceQuitAvailable: true);
        Assert.Equal("stuck", failed.FailureMessage);
        Assert.True(failed.ForceQuitAvailable);
        Assert.Null(AbortOutcome.Ok.FailureMessage);
    }

    [Fact]
    public void CloneOutcome_PathExistsExactlyOnSuccess()
    {
        Assert.Equal("/tmp/repo", new CloneOutcome.Cloned("/tmp/repo").RepoPath);
        Assert.Equal("boom", new CloneOutcome.Failed("boom").FailureMessage);
    }

    [Fact]
    public void Fetched_ImplicitConversion_WrapsValue()
    {
        Fetched<int> fetched = 42;
        var ok = Assert.IsType<Fetched<int>.Ok>(fetched);
        Assert.Equal(42, ok.Value);
    }

    [Fact]
    public void Fetched_Map_TransformsOkValue()
    {
        Fetched<int> fetched = 21;
        var mapped = fetched.Map(v => v * 2);
        var ok = Assert.IsType<Fetched<int>.Ok>(mapped);
        Assert.Equal(42, ok.Value);
    }

    [Fact]
    public void Fetched_Map_PreservesFailureDetail()
    {
        Fetched<int> fetched = new Fetched<int>.Failed("one line", "full\nblock");
        var mapped = fetched.Map(v => v * 2);
        var failed = Assert.IsType<Fetched<int>.Failed>(mapped);
        Assert.Equal("one line", failed.Message);
        Assert.Equal("full\nblock", failed.Detail);
    }

    [Fact]
    public void Fetched_Fail_ProducesFailedWithoutDetail()
    {
        var failed = Assert.IsType<Fetched<string>.Failed>(Fetched<string>.Fail("boom"));
        Assert.Equal("boom", failed.Message);
        Assert.Null(failed.Detail);
    }
}
