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
    public static UpdateManager CreateManager() =>
        new(new GithubSource("https://github.com/Zeejfps/GitBench", null, false),
            new UpdateOptions { ExplicitChannel = RuntimeChannel() });

    // The per-OS/arch channel must match the --channel vpk packs with in CI (see
    // .github/workflows/release.yml), or a check finds no matching release.
    private static string RuntimeChannel() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : "linux-x64";
}
