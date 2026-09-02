using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// What a server said on its way out. This is the only channel a server that died before speaking
/// the protocol has, and it is the difference between "it stopped" and a sentence the reader can
/// act on. Spawns real processes, because the thing under test is a pipe.
/// </summary>
public sealed class ServerComplaintTests
{
    private static LanguageServerEntry Entry(params string[] args) => EntryFor("/bin/sh", args);

    private static LanguageServerEntry EntryFor(string command, params string[] args) => new(
        LanguageId.Of("test"),
        command,
        Args: args,
        Extensions: [],
        RootMarkers: [],
        Environment: new Dictionary<string, string>(),
        InitializationOptionsJson: null,
        SettingsJson: null,
        RequestTimeout: TimeSpan.FromSeconds(2),
        IdleShutdown: TimeSpan.FromMinutes(5));

    private static ILanguageServerSession Launch(LanguageServerEntry entry)
    {
        var launcher = new ProcessLanguageServerLauncher(
            new MapServerEnvironment(new Dictionary<string, string>()),
            action => action());
        var launched = launcher.Launch(new ServerLaunchRequest(entry, Path.GetTempPath(), Path.GetTempPath()));
        return (ILanguageServerSession)Assert.IsType<LaunchResult.Started>(launched).Process;
    }

    [Fact]
    public async Task AServerThatDiesBeforeSpeakingIsQuotedOnTheWayOut()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;

        using var server = Launch(Entry("-c", "echo 'no such toolchain component' >&2; exit 1"));

        var failure = await server.HandshakeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Contains("no such toolchain component", failure);
    }

    // The tail, not the head: a server that logged for a minute and then fell over is worth reading
    // from the end.
    [Fact]
    public async Task TheLastThingSaidSurvivesAWallOfChatter()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;

        using var server = Launch(Entry(
            "-c", "for i in $(seq 1 200); do echo \"chatter $i\" >&2; done; echo 'the real problem' >&2; exit 1"));

        var failure = await server.HandshakeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Contains("the real problem", failure);
    }

    // A server that keeps logging must not be strangled by a pipe nobody empties.
    [Fact]
    public async Task AServerThatLogsHardEnoughToFillThePipeKeepsRunning()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;

        using var server = Launch(Entry(
            "-c", "for i in $(seq 1 20000); do echo \"noise line number $i padded out a bit\" >&2; done; " +
                  "sleep 30"));

        var handshake = server.HandshakeAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        var finished = await Task.WhenAny(handshake, Task.Delay(TimeSpan.FromSeconds(10)));

        // A blocked server never answers and never dies; the handshake has to come back on its own
        // timeout rather than the test's.
        Assert.Same(handshake, finished);
    }

    // The exit event fires before the last line written reaches us, and the supervisor's
    // give-up message is built from the exit, not from the handshake. Whichever of the two
    // notices first has to carry the same words.
    [Fact]
    public async Task TheExitItselfCarriesWhatTheServerSaid()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;

        // Enough output that the process is reaped well before the last line reaches the reader,
        // which is the race a single echo wins by luck.
        using var server = Launch(Entry(
            "-c", "for i in $(seq 1 4000); do echo \"chatter $i padded out a good deal\" >&2; done; " +
                  "echo 'no such toolchain component' >&2; exit 1"));
        var exited = new TaskCompletionSource<ServerExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Exited += exit => exited.TrySetResult(exit);

        var seen = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("no such toolchain component", seen.Detail);
    }
}
