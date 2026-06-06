using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;
using ZGF.Observable;

namespace GitBench;

/// <summary>
/// Owns the Velopack update check and the result of staging an update so the UI can offer a
/// one-click restart. <see cref="CheckForUpdatesAsync"/> drives both the silent startup check
/// (see Program.cs) and the manual status-bar button: it queries GitHub Releases off-thread,
/// downloads any staged update, then calls <see cref="OfferUpdate"/> on the UI thread. The
/// update banner binds to <see cref="BannerMessage"/> and invokes <see cref="ApplyAndRestart"/>
/// on click; the status bar binds to <see cref="IsChecking"/> (spinner) and
/// <see cref="CheckFeedback"/> (inline "up to date" / "failed" result of a manual check).
/// </summary>
public sealed class UpdateService
{
    private UpdateManager? _manager;
    private UpdateInfo? _update;

    /// <summary>Non-null once an update is staged; drives the banner's text and visibility.</summary>
    public State<string?> BannerMessage { get; } = new(null);

    /// <summary>True while a check is in flight; spins the status-bar check button.</summary>
    public State<bool> IsChecking { get; } = new(false);

    /// <summary>
    /// Brief inline result of a user-initiated check ("up to date" / "failed"), shown next to
    /// the status-bar button. Left null by the silent startup check, which only stages updates.
    /// </summary>
    public State<string?> CheckFeedback { get; } = new(null);

    /// <summary>
    /// Queries GitHub Releases and stages any newer build. The first <c>await</c> yields, so the
    /// network I/O runs off the UI thread; every observable mutation after that is marshaled back
    /// through <paramref name="dispatcher"/> (<see cref="State{T}"/> is single-threaded). Must be
    /// invoked on the UI thread. A no-op if a check is already running or an update is staged.
    /// <paramref name="userInitiated"/> gates the inline <see cref="CheckFeedback"/> so the
    /// startup check stays silent. Velopack only finds updates from an installed build, so a
    /// plain <c>dotnet run</c> quietly no-ops via the catch.
    /// </summary>
    public async Task CheckForUpdatesAsync(IUiDispatcher dispatcher, bool userInitiated)
    {
        // Already staged: the banner is already offering the restart — nothing to recheck.
        if (_update != null || IsChecking.Value) return;

        IsChecking.Value = true;
        CheckFeedback.Value = null;
        try
        {
            var source = new GithubSource("https://github.com/Zeejfps/GitBench", null, false);
            var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = RuntimeChannel() });
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                dispatcher.Post(() =>
                {
                    IsChecking.Value = false;
                    if (userInitiated) CheckFeedback.Value = "You're on the latest version";
                });
                return;
            }
            await manager.DownloadUpdatesAsync(update);
            // Marshal back to the UI thread — State<T>/views are single-threaded.
            dispatcher.Post(() =>
            {
                IsChecking.Value = false;
                OfferUpdate(manager, update);
            });
        }
        catch
        {
            // Offline, no published release for this channel yet, or a non-installed dev build.
            dispatcher.Post(() =>
            {
                IsChecking.Value = false;
                if (userInitiated) CheckFeedback.Value = "Update check failed";
            });
        }
    }

    /// <summary>Call on the UI thread after the update has been downloaded.</summary>
    public void OfferUpdate(UpdateManager manager, UpdateInfo update)
    {
        _manager = manager;
        _update = update;
        BannerMessage.Value = $"Version {update.TargetFullRelease.Version} is ready — click Restart to update.";
    }

    /// <summary>
    /// Terminates and relaunches into the staged update. Must run on the UI thread; Velopack
    /// exits the process here, so racing it against the render loop can crash on shutdown.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _update is null) return;
        _manager.ApplyUpdatesAndRestart(_update);
    }

    // The per-OS/arch channel must match the --channel vpk packs with in CI (see
    // .github/workflows/release.yml), or CheckForUpdatesAsync finds no matching release.
    private static string RuntimeChannel() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : "linux-x64";
}
