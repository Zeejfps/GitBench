using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using GitBench.Lsp.Tests.Fakes;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// Getting hold of the process to ask it something. The handle is the supervisor's to hand out and
/// to take away: a caller that kept one across a crash would be talking to a dead pipe, and one
/// that kept it across a restart would be talking to the previous server.
/// </summary>
public sealed class ServerHandleTests : IDisposable
{
    const string RustFile = "src/main.rs";

    readonly SupervisorHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    ILanguageServerProcess? Handle() =>
        _harness.Servers.ProcessFor(_harness.Repo.Id, LanguageId.Of("rust"));

    [Fact]
    public void AServerThatWasNeverStartedHasNoHandle() => Assert.Null(Handle());

    [Fact]
    public void AStartedServerHandsOutTheProcessThatWasLaunched()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));

        Assert.Same(_harness.Launcher.Last, Handle());
    }

    [Fact]
    public void ALaunchThatFailedHandsOutNothing()
    {
        _harness.Launcher.FailEveryLaunch("no such command");

        _harness.Servers.OpenFile(_harness.File(RustFile));

        Assert.Null(Handle());
    }

    [Fact]
    public void ACrashedServerHandsOutNothing()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));
        _harness.Launcher.Last.Crash();

        Assert.IsType<ServerState.Failed>(_harness.Servers.StateFor(_harness.File(RustFile)));
        Assert.Null(Handle());
    }

    // The handle follows the process, and a retry is a new process — so what the caller held
    // before must never be what it gets back afterwards.
    [Fact]
    public void AfterARetryTheHandleIsTheNewProcess()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));
        var first = _harness.Launcher.Last;
        first.Crash();

        _harness.Servers.Retry(_harness.File(RustFile));

        var second = _harness.Launcher.Last;
        Assert.NotSame(first, second);
        Assert.Same(second, Handle());
    }

    [Fact]
    public void AServerTheSupervisorStoppedHandsOutNothing()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));
        _harness.Servers.CloseRepository(_harness.Repo.Id);

        Assert.Null(Handle());
    }

    [Fact]
    public void OneLanguagesHandleIsNeverAnothersProcess()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));
        _harness.Servers.OpenFile(_harness.File("cmd/main.go"));

        Assert.Same(_harness.Launcher.For("rust"), Handle());
        Assert.Same(
            _harness.Launcher.For("go"),
            _harness.Servers.ProcessFor(_harness.Repo.Id, LanguageId.Of("go")));
    }

    [Fact]
    public void AnotherRepositorysServerIsNotThisRepositorysHandle()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));

        Assert.Null(_harness.Servers.ProcessFor(_harness.OtherRepo.Id, LanguageId.Of("rust")));
    }

    [Fact]
    public void StoppingAServerShutsItDownAndLeavesItStopped()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));
        var server = _harness.Launcher.Last;

        _harness.Servers.StopServer(_harness.Repo.Id, LanguageId.Of("rust"));

        Assert.Equal(1, server.ShutdownRequests);
        Assert.Null(Handle());
        Assert.IsType<ServerState.Stopped>(_harness.Servers.StateFor(_harness.File(RustFile)));
    }

    // Stopping is not banning: the next file of that language starts it again, which is what makes
    // the button safe to press.
    [Fact]
    public void AStoppedServerStartsAgainForTheNextFile()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));
        _harness.Servers.StopServer(_harness.Repo.Id, LanguageId.Of("rust"));

        _harness.Servers.OpenFile(_harness.File(RustFile));

        Assert.Equal(2, _harness.Launcher.Started.Count);
        Assert.NotNull(Handle());
    }

    // Stopping a server discards its record, and the record is what remembered which project it
    // was started in. Without that memory kept somewhere, asking to start it again did nothing at
    // all — the button was there and the server never came back.
    [Fact]
    public void AServerStoppedByHandStartsAgainWhenAskedTo()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));
        _harness.Servers.StopServer(_harness.Repo.Id, LanguageId.Of("rust"));

        _harness.Servers.RestartServer(_harness.Repo.Id, LanguageId.Of("rust"));

        Assert.Equal(2, _harness.Launcher.Started.Count);
        Assert.NotNull(Handle());
    }

    [Fact]
    public void StartingAGivenUpServerAgainLaunchesIt()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));
        _harness.Launcher.FailEveryLaunch("not installed");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (_harness.Launcher.Running.Count > 0) _harness.Launcher.Last.Crash();
            _harness.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.IsType<ServerState.Failed>(_harness.Servers.StateFor(_harness.File(RustFile)));
        _harness.Launcher.StopFailing();

        var state = _harness.Servers.RestartServer(_harness.Repo.Id, LanguageId.Of("rust"));

        Assert.IsType<ServerState.Starting>(state);
        Assert.NotNull(Handle());
    }

    [Fact]
    public void StartingAServerThatWasNeverRunningStartsItInTheRepository()
    {
        _harness.Servers.RestartServer(_harness.Repo.Id, LanguageId.Of("rust"));

        Assert.Single(_harness.Launcher.Started);
        Assert.NotNull(Handle());
    }

    // A server given up on is stopped for good, and something has to be able to see that it is not
    // there rather than asking it questions nothing will answer.
    [Fact]
    public void AServerGivenUpOnHandsOutNothing()
    {
        _harness.Servers.OpenFile(_harness.File(RustFile));
        _harness.Launcher.FailEveryLaunch("still broken");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (_harness.Launcher.Running.Count > 0) _harness.Launcher.Last.Crash();
            _harness.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.IsType<ServerState.Failed>(_harness.Servers.StateFor(_harness.File(RustFile)));
        Assert.Null(Handle());
    }
}
