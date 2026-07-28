using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace GitBench.App;

/// <summary>
/// Reads updates from a primary source and drops to a fallback when it cannot be reached. An
/// installed build asks the same feed URL for the rest of its life, so a feed that moves or breaks
/// would otherwise strand it permanently, with no way left to ship the fix. Both sources serve the
/// same releases, so which one answers is immaterial — the point is that one always does.
/// </summary>
internal sealed class FallbackUpdateSource : IUpdateSource
{
    private readonly IUpdateSource _primary;
    private readonly IUpdateSource _fallback;
    private IUpdateSource _servedFeed;

    public FallbackUpdateSource(IUpdateSource primary, IUpdateSource fallback)
    {
        _primary = primary;
        _fallback = fallback;
        _servedFeed = primary;
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagedUserId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        try
        {
            var feed = await _primary.GetReleaseFeed(logger, appId, channel, stagedUserId, latestLocalRelease);
            _servedFeed = _primary;
            return feed;
        }
        catch
        {
            _servedFeed = _fallback;
            return await _fallback.GetReleaseFeed(logger, appId, channel, stagedUserId, latestLocalRelease);
        }
    }

    /// <summary>
    /// Tries the source that served the feed first, then the other. They can disagree in one narrow
    /// case: a release published between the feed read and the download leaves the redirector
    /// pointing at a newer release that no longer carries the file just named.
    /// </summary>
    public async Task DownloadReleaseEntry(
        IVelopackLogger logger,
        VelopackAsset releaseEntry,
        string localFile,
        Action<int> progress,
        CancellationToken cancelToken = default)
    {
        var preferred = _servedFeed;
        try
        {
            await preferred.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
        }
        catch when (!cancelToken.IsCancellationRequested)
        {
            var other = ReferenceEquals(preferred, _primary) ? _fallback : _primary;
            await other.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
        }
    }
}
