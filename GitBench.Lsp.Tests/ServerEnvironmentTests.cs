using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// Finding the executable, and failing to. A server the user has installed but the app cannot see is
/// the common failure on a Mac launched from the desktop, and it has to end in a message rather than
/// in nothing happening.
/// </summary>
public sealed class ServerEnvironmentTests
{
    [Fact]
    public void ACommandThatCannotBeFoundFailsTheLaunchWithItsName()
    {
        var launcher = new ProcessLanguageServerLauncher(new NoCommands(), RunHere);

        var result = launcher.Launch(new ServerLaunchRequest(Entry("definitely-not-installed"), "/repo", "/repo"));

        var failed = Assert.IsType<LaunchResult.Failed>(result);
        Assert.Contains("definitely-not-installed", failed.Reason, StringComparison.Ordinal);
    }

    // A launch failure is not a crash: nothing was ever running to come back, so there is no process
    // handed out for a supervisor to wait on.
    [Fact]
    public void AFailedLaunchYieldsNoProcess() =>
        Assert.IsNotType<LaunchResult.Started>(
            new ProcessLanguageServerLauncher(new NoCommands(), RunHere)
                .Launch(new ServerLaunchRequest(Entry("nope"), "/repo", "/repo")));

    [Fact]
    public void AnAbsolutePathIsTakenAsGivenWhenItExists()
    {
        var file = Path.Combine(Path.GetTempPath(), $"lsp-env-{Guid.NewGuid():N}");
        File.WriteAllText(file, "");
        try
        {
            Assert.Equal(file, CurrentProcessEnvironment.Instance.ResolveCommand(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AnAbsolutePathThatIsNotThereResolvesToNothing() =>
        Assert.Null(CurrentProcessEnvironment.Instance.ResolveCommand(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyCommandResolvesToNothing(string command) =>
        Assert.Null(CurrentProcessEnvironment.Instance.ResolveCommand(command));

    private static LanguageServerEntry Entry(string command) => new(
        LanguageId.Of("rust"),
        command,
        Args: [],
        Extensions: [],
        RootMarkers: [],
        Environment: new Dictionary<string, string>(),
        InitializationOptionsJson: null,
        SettingsJson: null,
        RequestTimeout: TimeSpan.FromSeconds(5),
        IdleShutdown: TimeSpan.FromMinutes(5));

    private static void RunHere(Action action) => action();

    private sealed class NoCommands : IServerEnvironment
    {
        public IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>();

        public string? ResolveCommand(string command) => null;
    }
}
