using GitBench.App;
using Xunit;

namespace GitBench.Tests;

public sealed class StartupHealthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gitbench-health-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "startup.state");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void FirstEverLaunchIsNotCrashLooping()
    {
        var health = StartupHealth.BeginLaunch(Path_, "1.0.0");

        Assert.Equal(0, health.FailedLaunches);
        Assert.False(health.IsCrashLooping);
    }

    [Fact]
    public void LaunchThatReachesTheRunLoopClearsTheCount()
    {
        StartupHealth.BeginLaunch(Path_, "1.0.0").MarkHealthy();
        StartupHealth.BeginLaunch(Path_, "1.0.0").MarkHealthy();

        Assert.Equal(0, StartupHealth.BeginLaunch(Path_, "1.0.0").FailedLaunches);
    }

    [Fact]
    public void TwoLaunchesThatNeverReachTheRunLoopTripRecovery()
    {
        // Each BeginLaunch with no MarkHealthy is a launch that died during startup.
        StartupHealth.BeginLaunch(Path_, "1.0.0");
        Assert.False(StartupHealth.BeginLaunch(Path_, "1.0.0").IsCrashLooping);

        var third = StartupHealth.BeginLaunch(Path_, "1.0.0");

        Assert.Equal(2, third.FailedLaunches);
        Assert.True(third.IsCrashLooping);
    }

    [Fact]
    public void ANewVersionStartsFromACleanSlate()
    {
        StartupHealth.BeginLaunch(Path_, "1.0.0");
        StartupHealth.BeginLaunch(Path_, "1.0.0");

        // Recovery updated us: the failures belong to the version we just left behind.
        var updated = StartupHealth.BeginLaunch(Path_, "1.0.1");

        Assert.Equal(0, updated.FailedLaunches);
        Assert.False(updated.IsCrashLooping);
    }

    [Fact]
    public void AnUnreadableStateFileDoesNotBlockStartup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "garbage");

        var health = StartupHealth.BeginLaunch(Path_, "1.0.0");

        Assert.Equal(0, health.FailedLaunches);
        Assert.False(health.IsCrashLooping);
    }
}
