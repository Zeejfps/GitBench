using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using GitBench.Lsp.Tests.Fakes;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// When a server is allowed to exist at all. rust-analyzer costs 1.7 GB and half a minute, so the
/// rule is that opening a repository buys nothing and previewing a file buys exactly one server.
/// </summary>
public class ServerStartTests
{
    [Fact]
    public void ActivatingARepository_StartsNothing()
    {
        using var harness = new SupervisorHarness();

        harness.Servers.SetActiveRepository(harness.Repo);

        Assert.Empty(harness.Launcher.Started);
    }

    [Fact]
    public void OpeningTheFirstFileOfALanguage_StartsThatLanguagesServer()
    {
        using var harness = new SupervisorHarness();

        harness.Servers.OpenFile(harness.File("src/main.rs"));

        var request = Assert.Single(harness.Launcher.Requests);
        Assert.Equal("rust-analyzer", request.Entry.Command);
    }

    [Fact]
    public void OpeningASecondFileOfTheSameLanguage_ReusesTheRunningServer()
    {
        using var harness = new SupervisorHarness();
        harness.Servers.OpenFile(harness.File("src/main.rs"));

        harness.Servers.OpenFile(harness.File("src/other.rs"));

        Assert.Single(harness.Launcher.Started);
    }

    [Fact]
    public void OpeningAFileOfAnotherLanguage_StartsItsOwnServer()
    {
        using var harness = new SupervisorHarness();
        harness.Servers.OpenFile(harness.File("src/main.rs"));

        harness.Servers.OpenFile(harness.File("cmd/main.go"));

        Assert.Equal(new[] { "rust-analyzer", "gopls" }, harness.Launcher.Requests.Select(r => r.Entry.Command));
    }

    [Fact]
    public void OpeningAFileNoServerClaims_StartsNothingAndSaysItIsNotConfigured()
    {
        using var harness = new SupervisorHarness();

        var state = harness.Servers.OpenFile(harness.File("README.md"));

        Assert.IsType<ServerState.NotConfigured>(state);
        Assert.Empty(harness.Launcher.Started);
    }

    [Fact]
    public void WithNoConfigFile_NothingEverStarts()
    {
        // The whole feature costs nothing when unused: an empty config is the same as an absent one.
        using var harness = new SupervisorHarness();
        harness.Servers.ApplyConfig(LanguageServerConfig.Empty);

        var state = harness.Servers.OpenFile(harness.File("src/main.rs"));

        Assert.IsType<ServerState.NotConfigured>(state);
        Assert.Empty(harness.Launcher.Started);
    }

    [Fact]
    public void OpeningAFileWithNoActiveRepository_StartsNothing()
    {
        using var harness = new SupervisorHarness();
        harness.Servers.SetActiveRepository(null);

        harness.Servers.OpenFile(harness.File("src/main.rs"));

        Assert.Empty(harness.Launcher.Started);
    }

    [Fact]
    public void AServer_IsLaunchedInTheProjectRootRatherThanTheFilesDirectory()
    {
        using var harness = new SupervisorHarness();

        harness.Servers.OpenFile(harness.File("src/deep/main.rs"));

        Assert.Equal(harness.Repo.RootPath, Assert.Single(harness.Launcher.Requests).ProjectRoot);
    }

    [Fact]
    public void OpeningAFileOutsideTheRepository_NeverStartsAServerForIt()
    {
        // Go-to-definition lands in the standard library or a package cache. There is no project
        // out there, so there is nothing to start.
        using var harness = new SupervisorHarness();

        var state = harness.Servers.OpenFile(harness.FileIn(harness.OtherRepo, "src/lib.rs"));

        Assert.Empty(harness.Launcher.Started);
        Assert.IsType<ServerState.Stopped>(state);
    }

    [Fact]
    public void OpeningAFileOutsideTheRepository_IsAnsweredByTheServerAlreadyRunning()
    {
        using var harness = new SupervisorHarness();
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Launcher.Last.BecomeReady();

        var state = harness.Servers.OpenFile(harness.FileIn(harness.OtherRepo, "src/lib.rs"));

        Assert.Single(harness.Launcher.Started);
        Assert.IsType<ServerState.Ready>(state);
    }

    [Fact]
    public void SwitchingRepositories_RunsTheNewOnesServerAgainstItsOwnRoot()
    {
        using var harness = new SupervisorHarness();
        harness.Servers.OpenFile(harness.File("src/main.rs"));

        harness.Servers.SetActiveRepository(harness.OtherRepo);
        harness.Servers.OpenFile(harness.FileIn(harness.OtherRepo, "src/lib.rs"));

        Assert.Equal(
            new[] { harness.Repo.RootPath, harness.OtherRepo.RootPath },
            harness.Launcher.Requests.Select(r => r.ProjectRoot));
    }

    [Fact]
    public void StateFor_AFileWhoseServerHasNotStarted_DoesNotStartIt()
    {
        using var harness = new SupervisorHarness();

        var state = harness.Servers.StateFor(harness.File("src/main.rs"));

        Assert.IsType<ServerState.Stopped>(state);
        Assert.Empty(harness.Launcher.Started);
    }
}
