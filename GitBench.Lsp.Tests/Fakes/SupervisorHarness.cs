using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using Xunit;

namespace GitBench.Lsp.Tests.Fakes;

/// <summary>
/// A supervisor wired to a fake launcher and a clock the test owns, over a config with two servers
/// and no root markers — so nothing here touches a process, a timer, or the disk.
/// </summary>
public sealed class SupervisorHarness : IDisposable
{
    public const string TwoServers =
        """
        {
          "servers": {
            "rust": { "command": "rust-analyzer", "extensions": [".rs"] },
            "go":   { "command": "gopls",         "extensions": [".go"] }
          },
          "maxConcurrentServers": 2
        }
        """;

    public SupervisorHarness(string configJson = TwoServers, SupervisorPolicy? policy = null)
    {
        Config = Parsed(configJson);
        Servers = new LanguageServerSupervisor(Launcher, Clock, policy);
        Servers.ApplyConfig(Config);
        Servers.SetActiveRepository(Repo);
    }

    public TestClock Clock { get; } = new();

    public FakeLauncher Launcher { get; } = new();

    public LanguageServerSupervisor Servers { get; }

    public LanguageServerConfig Config { get; private set; }

    public Repository Repo { get; } = new(RepositoryId.New(), RootOf("main-repo"));

    public Repository OtherRepo { get; } = new(RepositoryId.New(), RootOf("other-repo"));

    /// <summary>A path inside the active repository. Never created — nothing here reads it.</summary>
    public string File(string relativePath) => Path.Combine(Repo.RootPath, relativePath);

    public string FileIn(Repository repository, string relativePath) =>
        Path.Combine(repository.RootPath, relativePath);

    public void Reconfigure(string configJson)
    {
        Config = Parsed(configJson);
        Servers.ApplyConfig(Config);
    }

    /// <summary>Moves the clock and pumps the supervisor, the way the app's frame loop does.</summary>
    public void Advance(TimeSpan amount)
    {
        Clock.Advance(amount);
        Servers.Tick();
    }

    public void Dispose() => Servers.Dispose();

    public static LanguageServerConfig Parsed(string json) =>
        Assert.IsType<ConfigParse.Loaded>(LanguageServerConfig.Parse(json)).Config;

    static string RootOf(string name) =>
        Path.Combine(Path.GetTempPath(), "gitbench-lsp-tests", name);
}
