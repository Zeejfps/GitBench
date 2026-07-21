using GitBench.App;
using Velopack;
using ZGF.Observable;

VelopackApp.Build().Run();

if (args.Contains("--update"))
{
    RecoveryUpdater.TryApplyLatest();
    return 0;
}

var health = StartupHealth.BeginLaunch(AppPaths.AppDataPath("startup.state"), AppVersion.Display);
if (health.IsCrashLooping)
{
    Console.Error.WriteLine(
        $"v{AppVersion.Display} failed to start {health.FailedLaunches} times in a row — checking for a newer release.");
    RecoveryUpdater.TryApplyLatest();
}

using var host = GitBenchAppHost.Create();

// Drained on the first tick, by which point the window has been created, given its icon, positioned
// and shown, and the run loop is pumping — every startup crash this guards against has already had
// its chance to happen.
host.App.Context.Require<IUiDispatcher>().Post(health.MarkHealthy);

host.Run();
return 0;
