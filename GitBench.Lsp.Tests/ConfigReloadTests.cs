using GitBench.Lsp.Lifecycle;
using GitBench.Lsp.Tests.Fakes;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// The user editing the config file while servers are running. The cost of getting this wrong is
/// asymmetric: restarting a server that did not need it throws away a 32-second index, and keeping
/// one that did leaves the app running a command the config no longer names.
/// </summary>
public class ConfigReloadTests
{
    static readonly SupervisorPolicy Patient = new() { ReadySilence = TimeSpan.FromHours(1) };

    const string RustFile = "src/main.rs";

    const string Rust =
        """
        { "servers": { "rust": { "command": "rust-analyzer", "extensions": [".rs"] } } }
        """;

    const string RustWithADifferentTimeout =
        """
        {
          "servers": {
            "rust": { "command": "rust-analyzer", "extensions": [".rs"], "requestTimeoutMs": 9000 }
          }
        }
        """;

    const string RustFromAnotherPlace =
        """
        {
          "servers": {
            "rust": { "command": "/opt/ra/rust-analyzer", "extensions": [".rs"] }
          }
        }
        """;

    const string RustDisabled =
        """
        {
          "servers": {
            "rust": { "enabled": false, "command": "rust-analyzer", "extensions": [".rs"] }
          }
        }
        """;

    const string RustAndGo =
        """
        {
          "servers": {
            "rust": { "command": "rust-analyzer", "extensions": [".rs"] },
            "go":   { "command": "gopls", "extensions": [".go"] }
          }
        }
        """;

    [Fact]
    public void ReloadingTheSameConfig_LeavesARunningServerAlone()
    {
        using var harness = new SupervisorHarness(Rust, Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;

        harness.Reconfigure(Rust);

        Assert.True(server.IsRunning);
        Assert.Single(harness.Launcher.Started);
    }

    [Fact]
    public void ChangingSomethingThatDoesNotAffectTheLaunch_KeepsTheWarmServer()
    {
        using var harness = new SupervisorHarness(Rust, Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Launcher.Last.BecomeReady();
        var server = harness.Launcher.Last;

        harness.Reconfigure(RustWithADifferentTimeout);

        Assert.True(server.IsRunning);
        Assert.IsType<ServerState.Ready>(harness.Servers.StateFor(harness.File(RustFile)));
    }

    [Fact]
    public void ChangingTheCommand_RestartsTheServerWithTheNewOne()
    {
        using var harness = new SupervisorHarness(Rust, Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var original = harness.Launcher.Last;

        harness.Reconfigure(RustFromAnotherPlace);

        Assert.False(original.IsRunning);
        Assert.Equal("/opt/ra/rust-analyzer", harness.Launcher.Requests[^1].Entry.Command);
    }

    [Fact]
    public void RemovingAServerFromTheConfig_StopsIt()
    {
        using var harness = new SupervisorHarness(RustAndGo, Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Servers.OpenFile(harness.File("cmd/main.go"));

        harness.Reconfigure(Rust);

        Assert.False(harness.Launcher.For("go").IsRunning);
        Assert.True(harness.Launcher.For("rust").IsRunning);
    }

    [Fact]
    public void DisablingAServer_StopsIt()
    {
        using var harness = new SupervisorHarness(Rust, Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;

        harness.Reconfigure(RustDisabled);

        Assert.False(server.IsRunning);
        Assert.IsType<ServerState.NotConfigured>(harness.Servers.StateFor(harness.File(RustFile)));
    }

    [Fact]
    public void AddingAServerToTheConfig_StartsNothingUntilAFileNeedsIt()
    {
        using var harness = new SupervisorHarness(Rust, Patient);

        harness.Reconfigure(RustAndGo);

        Assert.Empty(harness.Launcher.Started);
    }

    [Fact]
    public void EditingTheConfigAfterAFailure_LetsTheServerBeTriedAgain()
    {
        // Giving up is per-configuration. The user editing the file is them saying they changed
        // whatever was wrong.
        using var harness = new SupervisorHarness(Rust, Patient);
        harness.Launcher.FailEveryLaunch("rust-analyzer: not found");
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Launcher.StopFailing();

        harness.Reconfigure(RustFromAnotherPlace);
        var state = harness.Servers.OpenFile(harness.File(RustFile));

        Assert.IsType<ServerState.Starting>(state);
    }

    [Fact]
    public void ClaimingMoreFileExtensions_DoesNotRestartTheServer()
    {
        // Which files a server answers for is not something the process itself knows. Restarting
        // for it would cost an index to change a lookup table.
        using var harness = new SupervisorHarness(Rust, Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;

        harness.Reconfigure(
            """
            { "servers": { "rust": { "command": "rust-analyzer", "extensions": [".rs", ".rsx"] } } }
            """);

        Assert.True(server.IsRunning);
        Assert.Single(harness.Launcher.Started);
    }
}
