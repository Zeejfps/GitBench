namespace GitBench.App;

/// <summary>
/// Updates the app without standing any of it up — no GUI, no git, no fonts, nothing but the
/// release feed and Velopack. That is the whole point: a build that crashes during startup can
/// still reach this path and pull the release that fixes it. Entered from a crash loop
/// (see <see cref="StartupHealth"/>) or on demand with <c>--update</c>.
/// </summary>
internal static class RecoveryUpdater
{
    /// <summary>
    /// Downloads the newest release and restarts into it. Does not return if an update is applied.
    /// <c>false</c> means there was nothing to apply — already newest, offline, or a build Velopack
    /// doesn't manage — and the caller should carry on starting normally, since the crash it is
    /// recovering from may have nothing to do with the version.
    /// </summary>
    public static bool TryApplyLatest()
    {
        try
        {
            var manager = UpdateFeed.CreateManager();
            var update = manager.CheckForUpdatesAsync().GetAwaiter().GetResult();
            if (update is null)
            {
                Console.Error.WriteLine($"No update available; v{AppVersion.Display} is the newest release.");
                return false;
            }

            Console.Error.WriteLine($"Downloading v{update.TargetFullRelease.Version}...");
            manager.DownloadUpdatesAsync(update).GetAwaiter().GetResult();
            Console.Error.WriteLine($"Restarting into v{update.TargetFullRelease.Version}.");
            manager.ApplyUpdatesAndRestart(update);
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Update failed: {e.Message}");
            return false;
        }
    }
}
