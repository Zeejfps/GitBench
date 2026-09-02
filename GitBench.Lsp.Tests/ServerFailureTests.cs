using GitBench.Lsp.Lifecycle;
using GitBench.Lsp.Tests.Fakes;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// What happens after a server dies, which is: nothing, until the reader asks. A language server
/// stops because its command is wrong, its toolchain is missing, or its project defeated it, and
/// none of those are different the second time — so a client that restarts by itself only spends
/// the reader's time before telling them what a first failure already knew.
/// </summary>
public class ServerFailureTests
{
    static readonly SupervisorPolicy Policy = new() { ReadySilence = TimeSpan.FromMinutes(10) };

    const string RustFile = "src/main.rs";

    [Fact]
    public void AServerThatCrashes_FailsAtOnce()
    {
        using var harness = new SupervisorHarness(policy: Policy);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Launcher.Last.BecomeReady();

        harness.Launcher.Last.Crash();

        Assert.IsType<ServerState.Failed>(harness.Servers.StateFor(harness.File(RustFile)));
    }

    [Fact]
    public void AServerThatCrashed_DoesNotComeBackOnItsOwn()
    {
        using var harness = new SupervisorHarness(policy: Policy);
        harness.Servers.OpenFile(harness.File(RustFile));

        harness.Launcher.Last.Crash();
        harness.Advance(TimeSpan.FromHours(1));

        Assert.Single(harness.Launcher.Started);
        Assert.IsType<ServerState.Failed>(harness.Servers.StateFor(harness.File(RustFile)));
    }

    // However long it had been working. A crash an hour in is still a crash, and the reader is
    // still the one who decides whether starting it again is worth a wait.
    [Fact]
    public void AServerThatRanWellForALongTime_StillFailsWhenItCrashes()
    {
        using var harness = new SupervisorHarness(policy: Policy);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Launcher.Last.BecomeReady();
        harness.Advance(TimeSpan.FromHours(1));

        harness.Launcher.Last.Crash();

        Assert.IsType<ServerState.Failed>(harness.Servers.StateFor(harness.File(RustFile)));
        Assert.Single(harness.Launcher.Started);
    }

    // The exit code and whatever the server said on its way out are the whole of what the reader
    // has to go on, so both have to survive into the reason the dialog shows.
    [Fact]
    public void ACrash_CarriesItsExitCodeAndLastWords()
    {
        using var harness = new SupervisorHarness(policy: Policy);
        harness.Servers.OpenFile(harness.File(RustFile));

        harness.Launcher.Last.Crash(exitCode: 101);

        var failed = Assert.IsType<ServerState.Failed>(harness.Servers.StateFor(harness.File(RustFile)));
        Assert.Contains("101", failed.Reason);
        Assert.Contains("crashed", failed.Reason);
    }

    [Fact]
    public void ACommandThatIsNotInstalled_FailsAtOnce()
    {
        using var harness = new SupervisorHarness(policy: Policy);
        harness.Launcher.FailEveryLaunch("rust-analyzer: not found");

        var state = harness.Servers.OpenFile(harness.File(RustFile));
        harness.Advance(TimeSpan.FromMinutes(10));

        Assert.IsType<ServerState.Failed>(state);
        Assert.Single(harness.Launcher.Requests);
    }

    [Fact]
    public void AFailure_CarriesSomethingToShowTheUser()
    {
        using var harness = new SupervisorHarness(policy: Policy);
        harness.Launcher.FailEveryLaunch("rust-analyzer: not found");

        var failed = Assert.IsType<ServerState.Failed>(harness.Servers.OpenFile(harness.File(RustFile)));

        Assert.NotEmpty(failed.Reason);
    }

    [Fact]
    public void AFailedServer_IsNotStartedAgainByTimePassing()
    {
        using var harness = new SupervisorHarness(policy: Policy);
        harness.Launcher.FailEveryLaunch("rust-analyzer: not found");
        harness.Servers.OpenFile(harness.File(RustFile));

        harness.Launcher.StopFailing();
        harness.Advance(TimeSpan.FromHours(1));

        Assert.Empty(harness.Launcher.Started);
    }

    [Fact]
    public void AFailedServer_IsNotStartedAgainByOpeningAnotherFile()
    {
        using var harness = new SupervisorHarness(policy: Policy);
        harness.Launcher.FailEveryLaunch("rust-analyzer: not found");
        harness.Servers.OpenFile(harness.File(RustFile));

        harness.Launcher.StopFailing();
        harness.Servers.OpenFile(harness.File("src/lib.rs"));

        Assert.Empty(harness.Launcher.Started);
    }

    // Stopping for good is only bearable because saying "try it again" is one click away, for when
    // the reader has installed the thing that was missing.
    [Fact]
    public void AskingAgainAfterAFailure_StartsTheServerFresh()
    {
        using var harness = new SupervisorHarness(policy: Policy);
        harness.Launcher.FailEveryLaunch("rust-analyzer: not found");
        harness.Servers.OpenFile(harness.File(RustFile));

        harness.Launcher.StopFailing();
        var state = harness.Servers.Retry(harness.File(RustFile));

        Assert.IsType<ServerState.Starting>(state);
        Assert.Single(harness.Launcher.Started);
    }
}
