using GitBench.App;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The update feed is the one dependency an installed build can never be talked out of, so the
/// fallback that keeps it reachable is covered without a network. See
/// docs/plans/rename-safe-identity.md.
/// </summary>
public sealed class FallbackUpdateSourceTests
{
    private sealed class FakeSource(string name, bool feedFails = false, bool downloadFails = false) : IUpdateSource
    {
        public int FeedCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger, string? appId, string channel,
            Guid? stagedUserId = null, VelopackAsset? latestLocalRelease = null)
        {
            FeedCalls++;
            if (feedFails) throw new HttpRequestException(name);
            return Task.FromResult(new VelopackAssetFeed
            {
                Assets = [new VelopackAsset { PackageId = name, Version = new SemanticVersion(1, 0, 0) }]
            });
        }

        public Task DownloadReleaseEntry(
            IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken = default)
        {
            DownloadCalls++;
            if (downloadFails) throw new HttpRequestException(name);
            return Task.CompletedTask;
        }
    }

    private static Task<VelopackAssetFeed> Feed(FallbackUpdateSource source) =>
        source.GetReleaseFeed(null!, "DiffDino", "win-x64");

    private static Task Download(FallbackUpdateSource source, CancellationToken token = default) =>
        source.DownloadReleaseEntry(null!, new VelopackAsset(), "local.nupkg", _ => { }, token);

    [Fact]
    public async Task TheFallbackIsNotTouchedWhileThePrimaryAnswers()
    {
        var primary = new FakeSource("primary");
        var fallback = new FakeSource("fallback");

        var feed = await Feed(new FallbackUpdateSource(primary, fallback));

        Assert.Equal("primary", feed.Assets[0].PackageId);
        Assert.Equal(0, fallback.FeedCalls);
    }

    [Fact]
    public async Task AnUnreachablePrimaryFallsBackRatherThanFailingTheCheck()
    {
        var fallback = new FakeSource("fallback");

        var feed = await Feed(new FallbackUpdateSource(new FakeSource("primary", feedFails: true), fallback));

        Assert.Equal("fallback", feed.Assets[0].PackageId);
    }

    [Fact]
    public async Task BothSourcesDownAreStillAFailedCheck()
    {
        var source = new FallbackUpdateSource(
            new FakeSource("primary", feedFails: true),
            new FakeSource("fallback", feedFails: true));

        await Assert.ThrowsAsync<HttpRequestException>(() => Feed(source));
    }

    [Fact]
    public async Task TheDownloadGoesToWhicheverSourceServedTheFeed()
    {
        var primary = new FakeSource("primary", feedFails: true);
        var fallback = new FakeSource("fallback");
        var source = new FallbackUpdateSource(primary, fallback);

        await Feed(source);
        await Download(source);

        Assert.Equal(1, fallback.DownloadCalls);
        Assert.Equal(0, primary.DownloadCalls);
    }

    [Fact]
    public async Task ADownloadThatFailsRetriesTheOtherSource()
    {
        var fallback = new FakeSource("fallback");
        var source = new FallbackUpdateSource(new FakeSource("primary", downloadFails: true), fallback);

        await Feed(source);
        await Download(source);

        Assert.Equal(1, fallback.DownloadCalls);
    }

    [Fact]
    public async Task ACancelledDownloadIsNotRetried()
    {
        var fallback = new FakeSource("fallback");
        var source = new FallbackUpdateSource(new FakeSource("primary", downloadFails: true), fallback);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => Download(source, cts.Token));
        Assert.Equal(0, fallback.DownloadCalls);
    }
}
