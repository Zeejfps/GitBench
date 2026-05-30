using Velopack;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Holds the result of a background Velopack update check so the UI can offer a one-click
/// restart. A startup task (see Program.cs) checks GitHub Releases off-thread, downloads any
/// staged update, then calls <see cref="OfferUpdate"/> on the UI thread; the update banner
/// binds to <see cref="BannerMessage"/> and invokes <see cref="ApplyAndRestart"/> on click.
/// </summary>
public sealed class UpdateService
{
    private UpdateManager? _manager;
    private UpdateInfo? _update;

    /// <summary>Non-null once an update is staged; drives the banner's text and visibility.</summary>
    public State<string?> BannerMessage { get; } = new(null);

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
}
