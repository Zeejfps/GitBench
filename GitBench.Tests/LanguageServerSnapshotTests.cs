using GitBench.Features.LanguageServers;
using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// What the Files pane reads off one value: whether a file has a server at all, and what that
/// server is doing. Telling "still loading" from "broken" is the whole reason the state is a sum
/// type, and this is where the pane's side of that is decided.
/// </summary>
public sealed class LanguageServerSnapshotTests
{
    private static readonly RepositoryId Repo = RepositoryId.New();

    private static readonly LanguageServerConfig Config = Parsed(
        """
        {
          "servers": {
            "rust": { "command": "rust-analyzer", "extensions": [".rs"] },
            "go":   { "command": "gopls",         "extensions": [".go"] }
          }
        }
        """);

    private static LanguageServerConfig Parsed(string json) =>
        Assert.IsType<ConfigParse.Loaded>(LanguageServerConfig.Parse(json)).Config;

    private static LanguageServerSnapshot Snapshot(params ServerStatus[] servers) =>
        new(Config, servers, [], [], ConfigFileExists: true);

    private static ServerStatus Status(string language, ServerState state) =>
        new(Repo, LanguageId.Of(language), state);

    [Fact]
    public void AFileNoServerClaimsIsNotConfigured() =>
        Assert.IsType<ServerState.NotConfigured>(Snapshot().StateFor("/repo/README.md"));

    // The distinction the pane hangs on: nothing claims this file, versus something does and has
    // not been started yet. Both draw nothing today; only one of them ever will.
    [Fact]
    public void AClaimedFileWithNoServerRunningIsStopped() =>
        Assert.IsType<ServerState.Stopped>(Snapshot().StateFor("/repo/src/main.rs"));

    [Fact]
    public void AClaimedFileReportsItsOwnLanguagesServer()
    {
        var snapshot = Snapshot(
            Status("rust", new ServerState.Indexing(42)),
            Status("go", new ServerState.Failed("gopls is not installed.")));

        Assert.Equal(42, Assert.IsType<ServerState.Indexing>(snapshot.StateFor("/repo/src/main.rs")).PercentComplete);
        Assert.IsType<ServerState.Failed>(snapshot.StateFor("/repo/cmd/main.go"));
    }

    [Fact]
    public void AFileWithNoConfigAtAllIsNotConfigured()
    {
        var empty = LanguageServerSnapshot.Nothing;

        Assert.IsType<ServerState.NotConfigured>(empty.StateFor("/repo/src/main.rs"));
        Assert.False(empty.Handles("/repo/src/main.rs"));
    }

    [Fact]
    public void HandlesFollowsTheConfigAndNotWhatIsRunning()
    {
        var snapshot = Snapshot();

        Assert.True(snapshot.Handles("/repo/src/main.rs"));
        Assert.False(snapshot.Handles("/repo/notes.txt"));
    }

    [Fact]
    public void EveryConfiguredServerIsListedWithWhereItStands()
    {
        var snapshot = Snapshot(Status("rust", new ServerState.Ready()));

        var configured = snapshot.Configured;

        Assert.Equal(["rust", "go"], configured.Select(c => c.Entry.Language.Value));
        Assert.IsType<ServerState.Ready>(configured[0].State);
        Assert.IsType<ServerState.Stopped>(configured[1].State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyAServerWithAProcessCountsAsRunning(bool ready)
    {
        var snapshot = Snapshot(Status(
            "rust", ready ? new ServerState.Ready() : new ServerState.Failed("stopped")));

        Assert.Equal(ready, snapshot.Configured[0].IsRunning);
    }

    // A status for a repository this snapshot is not about must never be read as this one's: the
    // store filters by repository, and this is the assertion that the reading side agrees.
    [Fact]
    public void AStatusListWithOnlyAnotherLanguageLeavesThisOneStopped()
    {
        var snapshot = Snapshot(Status("go", new ServerState.Ready()));

        Assert.IsType<ServerState.Stopped>(snapshot.StateFor("/repo/src/main.rs"));
    }
}
