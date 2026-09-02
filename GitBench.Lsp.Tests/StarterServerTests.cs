using GitBench.Lsp.Configuration;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// What the settings card offers: which servers a repository could use, and the config text that
/// would run them. The text is checked by parsing it back — a starter config that the app's own
/// parser skips would be a button that quietly does nothing.
/// </summary>
public sealed class StarterServerTests
{
    static readonly LanguageServerConfig Nothing = LanguageServerConfig.Empty;

    static LanguageServerConfig Configured(string json) =>
        Assert.IsType<ConfigParse.Loaded>(LanguageServerConfig.Parse(json)).Config;

    static string[] Languages(IEnumerable<StarterServer> servers) =>
        servers.Select(s => s.Language.Value).ToArray();

    [Theory]
    [InlineData("Cargo.toml", "rust")]
    [InlineData("go.mod", "go")]
    [InlineData("go.work", "go")]
    [InlineData("tsconfig.json", "typescript")]
    [InlineData("package.json", "typescript")]
    [InlineData("pyproject.toml", "python")]
    [InlineData("CMakeLists.txt", "cpp")]
    [InlineData("build.zig", "zig")]
    public void AMarkerAtTheRootSuggestsItsServer(string marker, string language) =>
        Assert.Contains(language, Languages(StarterServers.SuggestFor([marker, "README.md"], Nothing)));

    // A C# project is named after its solution, so the thing to look for is a kind of file rather
    // than a name.
    [Fact]
    public void AProjectFileIsMatchedByItsExtension() =>
        Assert.Contains("csharp", Languages(StarterServers.SuggestFor(["GitBench.sln"], Nothing)));

    [Fact]
    public void ARepositoryWithNoMarkersSuggestsNothing() =>
        Assert.Empty(StarterServers.SuggestFor(["README.md", "LICENSE", ".gitignore"], Nothing));

    [Fact]
    public void MarkersAreMatchedWithoutRegardToCase() =>
        Assert.Contains("rust", Languages(StarterServers.SuggestFor(["cargo.toml"], Nothing)));

    // The whole point of the card: a gap is a language with no server, not a language with one.
    [Fact]
    public void AConfiguredLanguageIsNotSuggested()
    {
        var configured = Configured(
            """{ "servers": { "rust": { "command": "rust-analyzer", "extensions": [".rs"] } } }""");

        Assert.DoesNotContain("rust", Languages(StarterServers.SuggestFor(["Cargo.toml", "go.mod"], configured)));
        Assert.Contains("go", Languages(StarterServers.SuggestFor(["Cargo.toml", "go.mod"], configured)));
    }

    // A server switched off by hand is still configured. Suggesting it again would offer to write
    // an entry that is already there, one line below the "enabled": false the user meant.
    [Fact]
    public void ALanguageTurnedOffInTheConfigIsStillNotSuggested()
    {
        var configured = Configured(
            """
            {
              "servers": {
                "rust": { "enabled": false, "command": "rust-analyzer", "extensions": [".rs"] }
              }
            }
            """);

        Assert.DoesNotContain("rust", Languages(StarterServers.SuggestFor(["Cargo.toml"], configured)));
    }

    [Fact]
    public void EveryMarkerOfEveryStarterIsSuggestedByItself()
    {
        foreach (var server in StarterServers.All)
            foreach (var marker in server.DetectMarkers)
            {
                var name = marker.StartsWith('*') ? "Anything" + marker[1..] : marker;
                Assert.Contains(server.Language.Value, Languages(StarterServers.SuggestFor([name], Nothing)));
            }
    }

    [Fact]
    public void TheStarterConfigParsesBackToTheServersItOffered()
    {
        var offered = StarterServers.SuggestFor(["Cargo.toml", "go.mod"], Nothing);

        var parsed = Configured(StarterServers.ConfigText(offered));

        Assert.Equal(Languages(offered), parsed.Servers.Select(s => s.Language.Value).ToArray());
        foreach (var server in offered)
        {
            var entry = parsed.ServerFor(server.Language)!;
            Assert.Equal(server.Command, entry.Command);
            Assert.Equal(server.Args, entry.Args);
            Assert.Equal(server.Extensions, entry.Extensions);
            Assert.Equal(server.RootMarkers, entry.RootMarkers);
        }
    }

    [Fact]
    public void EveryStarterInTheCatalogueSurvivesBeingWrittenAndReadBack()
    {
        var parsed = Configured(StarterServers.ConfigText(StarterServers.All));

        Assert.Equal(StarterServers.All.Count, parsed.Servers.Count);
    }

    [Fact]
    public void TheStarterConfigNamesTheSchemaVersionTheAppReads() =>
        Assert.IsType<ConfigParse.Loaded>(
            LanguageServerConfig.Parse(StarterServers.ConfigText([StarterServers.All[0]])));

    // Pasting one entry into a file that already has servers is the only way to add to a config the
    // app must not rewrite, so the snippet has to be an entry rather than a file.
    [Fact]
    public void OneEntrySlotsIntoAConfigThatAlreadyExists()
    {
        var go = StarterServers.All.Single(s => s.Language.Value == "go");

        var merged = Configured(
            $$"""
            {
              "servers": {
                "rust": { "command": "rust-analyzer", "extensions": [".rs"] },
                {{StarterServers.EntryText(go)}}
              }
            }
            """);

        Assert.Equal(["rust", "go"], merged.Servers.Select(s => s.Language.Value).ToArray());
        Assert.Equal("gopls", merged.ServerFor(go.Language)!.Command);
    }

    [Fact]
    public void NoTwoStartersClaimTheSameExtension()
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in StarterServers.All)
            foreach (var extension in server.Extensions)
                Assert.True(claimed.Add(extension.Value), $"{extension} is claimed twice");
    }
}
