using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

namespace GitBench.App;

/// <summary>
/// The release feed the app updates from — shared by the in-app <see cref="UpdateService"/> and the
/// headless <see cref="RecoveryUpdater"/> so both look at exactly the same releases.
/// </summary>
internal static class UpdateFeed
{
    // Identity, not display: every installed build asks this exact URL for the rest of its life, so
    // it must never carry the product name and must never be retired. It redirects to wherever the
    // releases actually live, which is what lets the repo — or the whole host — move later. A second
    // app gets its own subdomain rather than a path under this one.
    private const string FeedUri = "https://updates.builtbyzee.com/";

    // Only reached when the redirector cannot be. Renaming the repo is safe (the GitHub API 301s to
    // /repositories/{id}/ and HttpClient follows); re-occupying the old name is not, and would
    // strand every build that predates the redirector.
    private const string GithubRepo = "https://github.com/Zeejfps/GitBench";

    public static UpdateManager CreateManager() =>
        new(new FallbackUpdateSource(new SimpleWebSource(FeedUri), new GithubSource(GithubRepo, null, false)),
            new UpdateOptions { ExplicitChannel = RuntimeChannel() });

    // The per-OS/arch channel must match the --channel vpk packs with in CI (see
    // .github/workflows/release.yml), or a check finds no matching release. It is the only value
    // that joins an install to its feed, so unlike the URLs above it can never change at all.
    private static string RuntimeChannel() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : "linux-x64";
}
