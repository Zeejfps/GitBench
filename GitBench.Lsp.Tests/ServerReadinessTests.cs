using GitBench.Lsp.Lifecycle;
using GitBench.Lsp.Tests.Fakes;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// The measured fact this whole layer has to respect: rust-analyzer finishes its handshake in 15 ms
/// and is then 32 seconds away from answering anything. A client that calls that "ready" reports a
/// working feature that answers nothing for half a minute.
/// </summary>
public class ServerReadinessTests
{
    static readonly SupervisorPolicy Patient = new() { ReadySilence = TimeSpan.FromMinutes(10) };

    [Fact]
    public void AJustStartedServer_IsStartingAndNotReady()
    {
        using var harness = new SupervisorHarness();

        var state = harness.Servers.OpenFile(harness.File("src/main.rs"));

        Assert.IsType<ServerState.Starting>(state);
    }

    [Fact]
    public void AFinishedHandshake_IsNotReady()
    {
        using var harness = new SupervisorHarness();
        harness.Servers.OpenFile(harness.File("src/main.rs"));

        harness.Launcher.Last.CompleteHandshake();

        Assert.IsNotType<ServerState.Ready>(harness.Servers.StateFor(harness.File("src/main.rs")));
    }

    [Fact]
    public void AServerReportingProgress_IsIndexingWithThatProgress()
    {
        using var harness = new SupervisorHarness();
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Launcher.Last.CompleteHandshake();

        harness.Launcher.Last.ReportIndexing(40);

        var indexing = Assert.IsType<ServerState.Indexing>(harness.Servers.StateFor(harness.File("src/main.rs")));
        Assert.Equal(40, indexing.PercentComplete);
    }

    [Fact]
    public void AServerReportingProgressWithoutAPercentage_IsStillIndexing()
    {
        // gopls reports work in progress with no number attached. "Indexing, unknown" is a state
        // the pane can draw; "ready" is not.
        using var harness = new SupervisorHarness();
        harness.Servers.OpenFile(harness.File("src/main.rs"));

        harness.Launcher.Last.ReportIndexing();

        var indexing = Assert.IsType<ServerState.Indexing>(harness.Servers.StateFor(harness.File("src/main.rs")));
        Assert.Null(indexing.PercentComplete);
    }

    [Fact]
    public void AServerThatHasAnsweredSomething_IsReady()
    {
        using var harness = new SupervisorHarness();
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Launcher.Last.CompleteHandshake();

        harness.Launcher.Last.BecomeReady();

        Assert.IsType<ServerState.Ready>(harness.Servers.StateFor(harness.File("src/main.rs")));
    }

    [Fact]
    public void AReadyServerThatStartsIndexingAgain_SaysSo()
    {
        // rust-analyzer re-checks in the background and goes back to being unable to answer. A
        // state that only moves forwards would lie about it.
        using var harness = new SupervisorHarness();
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Launcher.Last.BecomeReady();

        harness.Launcher.Last.ReportIndexing(10);

        Assert.IsType<ServerState.Indexing>(harness.Servers.StateFor(harness.File("src/main.rs")));
    }

    [Fact]
    public void AStateChange_IsAnnouncedSoThePaneCanRedraw()
    {
        using var harness = new SupervisorHarness();
        var announced = new List<ServerState>();
        harness.Servers.StatusChanged += status => announced.Add(status.State);

        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Launcher.Last.BecomeReady();

        Assert.Contains(announced, s => s is ServerState.Starting);
        Assert.Contains(announced, s => s is ServerState.Ready);
    }

    [Fact]
    public void AServerStillIndexingLongAfterTheHandshake_IsNotTreatedAsBroken()
    {
        // 32 seconds of silence-with-progress is rust-analyzer working, not rust-analyzer wedged.
        using var harness = new SupervisorHarness(policy: new SupervisorPolicy { ReadySilence = TimeSpan.FromSeconds(20) });
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Launcher.Last.CompleteHandshake();

        harness.Advance(TimeSpan.FromSeconds(15));
        harness.Launcher.Last.ReportIndexing(50);
        harness.Advance(TimeSpan.FromSeconds(15));
        harness.Launcher.Last.ReportIndexing(90);
        harness.Advance(TimeSpan.FromSeconds(15));

        Assert.IsType<ServerState.Indexing>(harness.Servers.StateFor(harness.File("src/main.rs")));
    }

    [Fact]
    public void AServerThatSaysNothingAtAll_IsGivenUpOnRatherThanShownStartingForever()
    {
        using var harness = new SupervisorHarness(policy: new SupervisorPolicy { ReadySilence = TimeSpan.FromSeconds(20) });
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        var server = harness.Launcher.Last;

        harness.Advance(TimeSpan.FromSeconds(30));

        Assert.IsType<ServerState.Failed>(harness.Servers.StateFor(harness.File("src/main.rs")));
        Assert.False(server.IsRunning);
    }

    [Fact]
    public void AReadyServerThatGoesQuiet_IsLeftAlone()
    {
        // Silence is only a symptom while the server is still starting. A ready server that nobody
        // asks anything is a ready server.
        using var harness = new SupervisorHarness(policy: new SupervisorPolicy { ReadySilence = TimeSpan.FromSeconds(20) });
        harness.Servers.OpenFile(harness.File("src/main.rs"));
        harness.Launcher.Last.BecomeReady();

        harness.Advance(TimeSpan.FromSeconds(60));

        Assert.IsType<ServerState.Ready>(harness.Servers.StateFor(harness.File("src/main.rs")));
    }

    [Fact]
    public void AServerHandshakingRepeatedlyWithoutIndexing_StaysStarting()
    {
        using var harness = new SupervisorHarness(policy: Patient);
        harness.Servers.OpenFile(harness.File("src/main.rs"));

        harness.Launcher.Last.CompleteHandshake();
        harness.Launcher.Last.CompleteHandshake();

        Assert.IsType<ServerState.Starting>(harness.Servers.StateFor(harness.File("src/main.rs")));
    }
}
