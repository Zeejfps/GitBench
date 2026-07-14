using GitBench.App;
using Velopack;
using ZGF.Observable;

// Must be the very first thing that runs: Velopack's install/update hooks are driven by special CLI
// args the installer/updater passes, and any file I/O or GUI work before this can leave an update
// half-applied.
VelopackApp.Build().Run();

if (args.Contains("--update"))
{
    RecoveryUpdater.TryApplyLatest();
    return 0;
}

// Everything below here can take the process down — the window, the icon, the GPU context — so the
// launch is recorded first. A build whose startup crashes on the user's machine but not ours has no
// other way back: it never reaches the in-app updater.
var health = StartupHealth.BeginLaunch(AppPaths.AppDataPath("startup.state"), AppVersion.Display);
if (health.IsCrashLooping)
{
    Console.Error.WriteLine(
        $"v{AppVersion.Display} failed to start {health.FailedLaunches} times in a row — checking for a newer release.");
    // Restarts the process if it finds one. Otherwise fall through and try to start anyway: a crash
    // that no release fixes shouldn't lock the user out of an app that might yet come up.
    RecoveryUpdater.TryApplyLatest();
}

using var host = GitBenchHost.Create();

// Drained on the first tick, by which point the window has been created, given its icon, positioned
// and shown, and the run loop is pumping — every startup crash this guards against has already had
// its chance to happen.
host.Services.Require<IUiDispatcher>().Post(health.MarkHealthy);

host.Run();
return 0;
