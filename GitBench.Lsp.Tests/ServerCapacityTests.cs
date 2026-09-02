using GitBench.Lsp.Lifecycle;
using GitBench.Lsp.Tests.Fakes;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// The cap on how many servers run at once, which is the only thing standing between a user with
/// three languages open and 5 GB of resident memory.
/// </summary>
public class ServerCapacityTests
{
    static readonly SupervisorPolicy Patient = new() { ReadySilence = TimeSpan.FromHours(1) };

    const string ThreeLanguages =
        """
        {
          "servers": {
            "rust":       { "command": "rust-analyzer", "extensions": [".rs"] },
            "go":         { "command": "gopls", "extensions": [".go"] },
            "typescript": { "command": "typescript-language-server", "extensions": [".ts"] }
          },
          "maxConcurrentServers": 2
        }
        """;

    const string OneAtATime =
        """
        {
          "servers": {
            "rust": { "command": "rust-analyzer", "extensions": [".rs"] },
            "go":   { "command": "gopls", "extensions": [".go"] }
          },
          "maxConcurrentServers": 1
        }
        """;

    [Fact]
    public void OpeningMoreLanguagesThanTheLimit_LeavesOnlyTheLimitRunning()
    {
        using var harness = new SupervisorHarness(ThreeLanguages, Patient);

        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Servers.OpenFile(harness.File("cmd/main.go"));
        harness.Servers.OpenFile(harness.File("web/app.ts"));

        Assert.Equal(2, harness.Launcher.Running.Count);
    }

    [Fact]
    public void WithinOneRepository_TheServerNothingHasAskedAboutLongest_IsTheOneEvicted()
    {
        using var harness = new SupervisorHarness(ThreeLanguages, Patient);
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Advance(TimeSpan.FromMinutes(1));
        harness.Servers.OpenFile(harness.File("cmd/main.go"));

        harness.Advance(TimeSpan.FromMinutes(1));
        harness.Servers.OpenFile(harness.File("src/other.rs"));
        harness.Servers.OpenFile(harness.File("web/app.ts"));

        Assert.False(harness.Launcher.For("go").IsRunning);
        Assert.True(harness.Launcher.For("rust").IsRunning);
    }

    [Fact]
    public void WhenTheLimitIsReached_AServerForARepositoryTheUserLeft_GoesBeforeTheActiveOne()
    {
        using var harness = new SupervisorHarness(OneAtATime, Patient);
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        var abandoned = harness.Launcher.Last;

        harness.Servers.SetActiveRepository(harness.OtherRepo);
        harness.Servers.OpenFile(harness.FileIn(harness.OtherRepo, "src/main.rs"));

        Assert.False(abandoned.IsRunning);
        Assert.True(harness.Launcher.Last.IsRunning);
    }

    [Fact]
    public void AServerForARepositoryTheUserLeft_GoesEvenIfItWasUsedMoreRecently()
    {
        // Recency alone would evict the server the user is about to ask a question of, and keep one
        // for a repository they are no longer looking at.
        using var harness = new SupervisorHarness(ThreeLanguages, Patient);
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        var active = harness.Launcher.Last;

        harness.Advance(TimeSpan.FromMinutes(1));
        harness.Servers.SetActiveRepository(harness.OtherRepo);
        harness.Servers.OpenFile(harness.FileIn(harness.OtherRepo, "src/main.rs"));
        var abandoned = harness.Launcher.Last;

        harness.Advance(TimeSpan.FromMinutes(1));
        harness.Servers.SetActiveRepository(harness.Repo);
        harness.Servers.OpenFile(harness.File("cmd/main.go"));

        Assert.True(active.IsRunning, "The server for the repository the user is in was evicted.");
        Assert.False(abandoned.IsRunning);
    }

    [Fact]
    public void AnEvictedServer_IsAskedToStopRatherThanAbandoned()
    {
        // A process nobody asked to stop keeps its 1.7 GB, and the cap becomes a number in a file.
        using var harness = new SupervisorHarness(OneAtATime, Patient);
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        var evicted = harness.Launcher.Last;

        harness.Servers.OpenFile(harness.File("cmd/main.go"));

        Assert.Equal(1, evicted.ShutdownRequests);
    }

    [Fact]
    public void LoweringTheLimitInTheConfig_StopsTheServersThatNoLongerFit()
    {
        using var harness = new SupervisorHarness(ThreeLanguages, Patient);
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Servers.OpenFile(harness.File("cmd/main.go"));

        harness.Reconfigure(OneAtATime);

        Assert.Single(harness.Launcher.Running);
    }

    [Fact]
    public void AServerThatWasEvicted_StartsAgainWhenItsFileComesBack()
    {
        using var harness = new SupervisorHarness(OneAtATime, Patient);
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Servers.OpenFile(harness.File("cmd/main.go"));

        var state = harness.Servers.OpenFile(harness.File("src/main.rs"));

        Assert.IsType<ServerState.Starting>(state);
        Assert.Equal(3, harness.Launcher.Started.Count);
        Assert.Single(harness.Launcher.Running);
    }
}
