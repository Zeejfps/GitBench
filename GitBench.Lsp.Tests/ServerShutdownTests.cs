using GitBench.Lsp.Lifecycle;
using GitBench.Lsp.Tests.Fakes;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// Stopping servers, which is where the memory comes back. A server the user has walked away from
/// holds gigabytes; one that ignores a polite shutdown holds them forever.
/// </summary>
public class ServerShutdownTests
{
    static readonly TimeSpan IdleTime = TimeSpan.FromMinutes(5);   // the config default
    static readonly SupervisorPolicy Patient = new() { ReadySilence = TimeSpan.FromHours(1) };

    const string RustFile = "src/main.rs";

    [Fact]
    public void AServerForARepositoryTheUserLeft_StopsOnceItHasBeenIdleLongEnough()
    {
        using var harness = new SupervisorHarness(policy: Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;

        harness.Servers.SetActiveRepository(harness.OtherRepo);
        harness.Advance(IdleTime + TimeSpan.FromMinutes(1));

        Assert.False(server.IsRunning);
        Assert.Empty(harness.Servers.Status);
    }

    [Fact]
    public void AServerForTheRepositoryTheUserIsLookingAt_IsNotStoppedForBeingQuiet()
    {
        // Reading one file for ten minutes is not a reason to throw away a 32-second index.
        using var harness = new SupervisorHarness(policy: Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;

        harness.Advance(IdleTime * 3);

        Assert.True(server.IsRunning);
    }

    [Fact]
    public void ComingBackToARepositoryBeforeItsServerGoesIdle_KeepsTheWarmServer()
    {
        using var harness = new SupervisorHarness(policy: Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;

        harness.Servers.SetActiveRepository(harness.OtherRepo);
        harness.Advance(IdleTime - TimeSpan.FromMinutes(1));
        harness.Servers.SetActiveRepository(harness.Repo);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Advance(IdleTime - TimeSpan.FromMinutes(1));

        Assert.True(server.IsRunning);
        Assert.Single(harness.Launcher.Started);
    }

    [Fact]
    public void ClosingARepository_StopsItsServersWithoutWaitingForTheIdleTime()
    {
        using var harness = new SupervisorHarness(policy: Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;
        server.BecomeReady();

        harness.Servers.CloseRepository(harness.Repo.Id);

        Assert.False(server.IsRunning);
        Assert.Equal(1, server.ShutdownRequests);
    }

    [Fact]
    public void ClosingARepository_LeavesAnotherRepositorysServerAlone()
    {
        using var harness = new SupervisorHarness(policy: Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var first = harness.Launcher.Last;
        harness.Servers.SetActiveRepository(harness.OtherRepo);
        harness.Servers.OpenFile(harness.FileIn(harness.OtherRepo, RustFile));
        var second = harness.Launcher.Last;

        harness.Servers.CloseRepository(harness.Repo.Id);

        Assert.False(first.IsRunning);
        Assert.True(second.IsRunning);
    }

    [Fact]
    public void AServerThatExitsWhenAsked_IsNotKilled()
    {
        using var harness = new SupervisorHarness(policy: Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;

        harness.Servers.CloseRepository(harness.Repo.Id);
        harness.Advance(TimeSpan.FromMinutes(1));

        Assert.False(server.WasKilled);
    }

    [Fact]
    public void AServerThatIgnoresTheShutdownRequest_IsKilled()
    {
        using var harness = new SupervisorHarness(policy: new SupervisorPolicy
        {
            ShutdownGrace = TimeSpan.FromSeconds(5),
            ReadySilence = TimeSpan.FromHours(1),
        });
        harness.Launcher.Configure = server => server.IgnoresShutdown = true;
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;

        harness.Servers.CloseRepository(harness.Repo.Id);
        Assert.False(server.WasKilled);
        harness.Advance(TimeSpan.FromSeconds(10));

        Assert.True(server.WasKilled);
    }

    [Fact]
    public void ARepositoryClosedWhileItsServerIsStillStarting_LeavesNothingRunning()
    {
        using var harness = new SupervisorHarness(policy: Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;

        harness.Servers.CloseRepository(harness.Repo.Id);

        Assert.False(server.IsRunning);
        Assert.Empty(harness.Servers.Status);
    }

    [Fact]
    public void AServerReportingItselfReadyAfterItsRepositoryClosed_DoesNotComeBackToLife()
    {
        // The handshake is in flight when the user closes the repository. Whatever it says when it
        // lands, there is no repository to say it about.
        using var harness = new SupervisorHarness(policy: Patient);
        harness.Launcher.Configure = started => started.IgnoresShutdown = true;
        harness.Servers.OpenFile(harness.File(RustFile));
        var server = harness.Launcher.Last;
        harness.Servers.CloseRepository(harness.Repo.Id);

        server.BecomeReady();

        Assert.Empty(harness.Servers.Status);
    }

    [Fact]
    public void DisposingTheSupervisor_AsksEveryServerToStop()
    {
        var harness = new SupervisorHarness(policy: Patient);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Servers.OpenFile(harness.File("cmd/main.go"));
        var servers = harness.Launcher.Started.ToList();

        harness.Dispose();

        Assert.All(servers, server => Assert.False(server.IsRunning));
    }
}
