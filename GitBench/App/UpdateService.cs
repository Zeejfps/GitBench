using System.Runtime.InteropServices;
using GitBench.Localization;
using Velopack;
using Velopack.Sources;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Owns the Velopack update check and the result of staging an update so the UI can offer a
/// one-click restart. <see cref="CheckForUpdatesAsync"/> drives both the silent startup check
/// (see Program.cs) and the manual status-bar button: it queries GitHub Releases off-thread,
/// downloads any staged update, then calls <see cref="OfferUpdate"/> on the UI thread. The
/// update banner binds to <see cref="BannerMessage"/> and invokes <see cref="ApplyAndRestart"/>
/// on click; the status bar binds to <see cref="IsChecking"/> (spinner) and
/// <see cref="CheckFeedback"/> (inline "up to date" / "failed" result of a manual check).
/// <see cref="StartAutoChecks"/> adds a periodic silent recheck on top of the one-shot startup
/// check, gated by <see cref="AutoCheckEnabled"/> so a future settings toggle can turn it off.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly ILocalizationService _loc;
    private readonly CancellationTokenSource _autoCheckCts = new();
    private UpdateManager? _manager;
    private UpdateInfo? _update;
    private bool _autoChecksStarted;

    public UpdateService(ILocalizationService loc)
    {
        _loc = loc;
    }

    /// <summary>
    /// Gates the periodic background check. On by default; a future settings screen binds a
    /// checkbox here to let the user disable automatic update checks. Read/written on the UI
    /// thread only. The one-shot startup check and the manual button are unaffected.
    /// </summary>
    public State<bool> AutoCheckEnabled { get; } = new(true);

    /// <summary>
    /// How long <see cref="StartAutoChecks"/> waits between background rechecks. Sampled once
    /// when the loop starts; changing it afterward doesn't reschedule the running timer.
    /// </summary>
    public TimeSpan AutoCheckInterval { get; set; } = TimeSpan.FromHours(6);

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
                    if (userInitiated) CheckFeedback.Value = _loc.Strings.Value.AppUpdateUpToDate;
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
                if (userInitiated) CheckFeedback.Value = _loc.Strings.Value.AppUpdateCheckFailed;
            });
        }
    }

    /// <summary>
    /// Starts the periodic silent recheck loop (idempotent). The one-shot startup check in
    /// Program.cs runs first; this then rechecks every <see cref="AutoCheckInterval"/> until the
    /// service is disposed. Each tick re-reads <see cref="AutoCheckEnabled"/> on the UI thread, so
    /// disabling the toggle stops further checks without tearing the loop down. Must be called on
    /// the UI thread. Once an update is staged the underlying check no-ops, so the loop naturally
    /// idles after the banner appears.
    /// </summary>
    public void StartAutoChecks(IUiDispatcher dispatcher)
    {
        if (_autoChecksStarted) return;
        _autoChecksStarted = true;
        _ = RunAutoCheckLoopAsync(dispatcher);
    }

    private async Task RunAutoCheckLoopAsync(IUiDispatcher dispatcher)
    {
        using var timer = new PeriodicTimer(AutoCheckInterval);
        try
        {
            // First tick fires one interval from now — the startup check already covered t=0.
            while (await timer.WaitForNextTickAsync(_autoCheckCts.Token).ConfigureAwait(false))
            {
                // Marshal back to the UI thread: AutoCheckEnabled and the State<T>s that
                // CheckForUpdatesAsync mutates are single-threaded.
                dispatcher.Post(() =>
                {
                    if (!AutoCheckEnabled.Value) return;
                    _ = CheckForUpdatesAsync(dispatcher, userInitiated: false);
                });
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
    }

    /// <summary>Call on the UI thread after the update has been downloaded.</summary>
    public void OfferUpdate(UpdateManager manager, UpdateInfo update)
    {
        _manager = manager;
        _update = update;
        BannerMessage.Value = _loc.Strings.Value.AppUpdateReadyMessage(update.TargetFullRelease.Version);
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

    public void Dispose()
    {
        _autoCheckCts.Cancel();
        _autoCheckCts.Dispose();
        AutoCheckEnabled.Dispose();
        BannerMessage.Dispose();
        IsChecking.Dispose();
        CheckFeedback.Dispose();
    }

    // The per-OS/arch channel must match the --channel vpk packs with in CI (see
    // .github/workflows/release.yml), or CheckForUpdatesAsync finds no matching release.
    private static string RuntimeChannel() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : "linux-x64";
}
