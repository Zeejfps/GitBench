using GitBench.Lsp.Configuration;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// The config file is the only file in the app a person writes by hand, and it names programs to
/// run. These pin the boundary: what survives a mistake, what is refused outright, and what is
/// never guessed at.
/// </summary>
public class ConfigParseTests
{
    const string PlanExample =
        """
        {
          "version": 1,
          "servers": {
            "rust": {
              "enabled": true,
              "command": "rust-analyzer",
              "args": ["--log-file", "/tmp/ra.log"],
              "extensions": [".rs"],
              "rootMarkers": ["Cargo.toml"],
              "env": { "RA_LOG": "info" },
              "initializationOptions": { "cachePriming": { "enable": false } },
              "settings": { "checkOnSave": true },
              "requestTimeoutMs": 5000,
              "idleShutdownMs": 300000
            }
          },
          "maxConcurrentServers": 2
        }
        """;

    [Fact]
    public void Parse_AFullEntry_ReadsEveryFieldTheServerNeedsToLaunch()
    {
        var entry = Assert.Single(Loaded(PlanExample).Config.Servers);

        Assert.Equal("rust", entry.Language.Value);
        Assert.Equal("rust-analyzer", entry.Command);
        Assert.Equal(new[] { "--log-file", "/tmp/ra.log" }, entry.Args);
        Assert.Equal(new[] { ".rs" }, entry.Extensions.Select(e => e.Value));
        Assert.Equal(new[] { "Cargo.toml" }, entry.RootMarkers);
        Assert.Equal("info", entry.Environment["RA_LOG"]);
        Assert.Equal(TimeSpan.FromSeconds(5), entry.RequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), entry.IdleShutdown);
    }

    [Fact]
    public void Parse_ServerSuppliedJson_IsKeptVerbatimRatherThanReshaped()
    {
        // initializationOptions and settings are handed back to the server as it wrote them. The
        // client has no business understanding either.
        var entry = Assert.Single(Loaded(PlanExample).Config.Servers);

        Assert.Contains("cachePriming", entry.InitializationOptionsJson);
        Assert.Contains("checkOnSave", entry.SettingsJson);
    }

    [Fact]
    public void Parse_AnEntryWithOnlyTheRequiredFields_GetsWorkableDefaults()
    {
        var entry = Assert.Single(Loaded(
            """
            { "servers": { "go": { "command": "gopls", "extensions": [".go"] } } }
            """).Config.Servers);

        Assert.Empty(entry.Args);
        Assert.Empty(entry.RootMarkers);
        Assert.Empty(entry.Environment);
        Assert.Null(entry.SettingsJson);
        Assert.True(entry.RequestTimeout > TimeSpan.Zero);
        Assert.True(entry.IdleShutdown > TimeSpan.Zero);
    }

    [Fact]
    public void Parse_CommentsAndTrailingCommas_AreAllowedBecauseAPersonWritesThisFile()
    {
        var loaded = Loaded(
            """
            {
              // the one language I care about
              "servers": {
                "rust": {
                  "command": "rust-analyzer",
                  "extensions": [".rs"], /* nothing else yet */
                },
              },
            }
            """);

        Assert.Single(loaded.Config.Servers);
        Assert.Empty(loaded.Problems);
    }

    [Fact]
    public void Parse_LanguageKeyCasing_DoesNotMakeADifferentServer()
    {
        var entry = Assert.Single(Loaded(
            """
            { "servers": { "Rust": { "command": "rust-analyzer", "extensions": [".rs"] } } }
            """).Config.Servers);

        Assert.Equal("rust", entry.Language.Value);
    }

    [Fact]
    public void Parse_FieldsThisVersionDoesNotKnow_AreIgnoredRatherThanFatal()
    {
        var loaded = Loaded(
            """
            {
              "servers": {
                "rust": { "command": "rust-analyzer", "extensions": [".rs"], "traceLevel": "verbose" }
              },
              "somethingNewer": true
            }
            """);

        Assert.Single(loaded.Config.Servers);
        Assert.Empty(loaded.Problems);
    }

    [Fact]
    public void Parse_AnEmptyFile_IsNoServersRatherThanAFailure()
    {
        var loaded = Loaded("   \n  ");

        Assert.Empty(loaded.Config.Servers);
        Assert.Empty(loaded.Problems);
    }

    [Fact]
    public void Parse_AFileWithNoServersSection_IsNoServers()
    {
        Assert.Empty(Loaded("""{ "version": 1 }""").Config.Servers);
    }

    [Fact]
    public void Parse_ADisabledEntry_IsNotAServer()
    {
        var loaded = Loaded(
            """
            {
              "servers": {
                "rust": { "enabled": false, "command": "rust-analyzer", "extensions": [".rs"] },
                "go":   { "command": "gopls", "extensions": [".go"] }
              }
            }
            """);

        Assert.Equal(new[] { "go" }, loaded.Config.Servers.Select(s => s.Language.Value));
        Assert.Empty(loaded.Problems);
    }

    [Theory]
    [InlineData("""{ "extensions": [".rs"] }""")]                                  // no command
    [InlineData("""{ "command": "  ", "extensions": [".rs"] }""")]                 // blank command
    [InlineData("""{ "command": "rust-analyzer" }""")]                             // no extensions
    [InlineData("""{ "command": "rust-analyzer", "extensions": [] }""")]           // empty extensions
    [InlineData("""{ "command": "rust-analyzer", "extensions": ["*.rs"] }""")]     // not an extension
    [InlineData("""{ "command": "rust-analyzer", "extensions": ".rs" }""")]        // not a list
    [InlineData("""{ "command": 7, "extensions": [".rs"] }""")]                    // wrong type
    [InlineData("""{ "command": "ra", "extensions": [".rs"], "args": "--stdio" }""")]
    [InlineData("""{ "command": "ra", "extensions": [".rs"], "enabled": "yes" }""")]
    [InlineData("""{ "command": "ra", "extensions": [".rs"], "env": { "N": 1 } }""")]
    [InlineData("""{ "command": "ra", "extensions": [".rs"], "settings": [] }""")]
    [InlineData("""{ "command": "ra", "extensions": [".rs"], "requestTimeoutMs": 0 }""")]
    [InlineData("""{ "command": "ra", "extensions": [".rs"], "idleShutdownMs": -1 }""")]
    [InlineData("""[ "rust-analyzer" ]""")]                                        // not an object
    public void Parse_AnEntryThatIsNotUsable_IsSkippedWithAReasonAndTheRestSurvives(string rustEntry)
    {
        var loaded = Loaded(
            $$"""
            {
              "servers": {
                "rust": {{rustEntry}},
                "go": { "command": "gopls", "extensions": [".go"] }
              }
            }
            """);

        Assert.Equal(new[] { "go" }, loaded.Config.Servers.Select(s => s.Language.Value));
        AssertProblemAbout("rust", loaded.Problems);
    }

    [Fact]
    public void Parse_ASyntaxError_NamesTheLineItIsOnRatherThanBeingSwallowed()
    {
        var error = Assert.IsType<ConfigParse.NotUnderstood>(LanguageServerConfig.Parse(
            """
            {
              "servers": {
                "rust": { "command": "rust-analyzer" "extensions": [".rs"] }
              }
            }
            """)).Error;

        Assert.Equal(3, error.Line);
        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public void Parse_TextThatIsNotJsonAtAll_IsRefusedWholeRatherThanReadAsEmpty()
    {
        // Silently reading a corrupt file as "no servers configured" is the failure mode where the
        // feature looks disabled and nobody knows why.
        Assert.IsType<ConfigParse.NotUnderstood>(LanguageServerConfig.Parse("servers: rust-analyzer"));
    }

    [Fact]
    public void Parse_JsonOfTheWrongShape_IsRefusedWithAMessage()
    {
        var error = Assert.IsType<ConfigParse.NotUnderstood>(
            LanguageServerConfig.Parse("""["rust-analyzer"]""")).Error;

        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public void Parse_AVersionFromTheFuture_IsRefusedRatherThanReadAsIfItWereThisOne()
    {
        var unsupported = Assert.IsType<ConfigParse.Unsupported>(LanguageServerConfig.Parse(
            """
            {
              "version": 99,
              "servers": { "rust": { "command": "rust-analyzer", "extensions": [".rs"] } }
            }
            """));

        Assert.Equal(99, unsupported.FileVersion);
        Assert.True(unsupported.HighestSupported < unsupported.FileVersion);
    }

    [Fact]
    public void Parse_NoVersionField_IsReadAsTheCurrentSchema()
    {
        Assert.Single(Loaded(
            """
            { "servers": { "rust": { "command": "rust-analyzer", "extensions": [".rs"] } } }
            """).Config.Servers);
    }

    [Fact]
    public void Parse_AVersionOfTheWrongType_IsReportedWithoutLosingTheServers()
    {
        var loaded = Loaded(
            """
            {
              "version": "one",
              "servers": { "rust": { "command": "rust-analyzer", "extensions": [".rs"] } }
            }
            """);

        Assert.Single(loaded.Config.Servers);
        AssertProblemAbout("version", loaded.Problems);
    }

    [Fact]
    public void Parse_MaxConcurrentServers_DefaultsWhenAbsent()
    {
        var config = Loaded("""{ "servers": {} }""").Config;

        Assert.True(config.MaxConcurrentServers >= 1);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("\"lots\"")]
    public void Parse_AnUnusableServerLimit_FallsBackToTheDefaultAndSaysSo(string value)
    {
        var loaded = Loaded($$"""{ "servers": {}, "maxConcurrentServers": {{value}} }""");

        Assert.Equal(LanguageServerConfig.DefaultMaxConcurrentServers, loaded.Config.MaxConcurrentServers);
        AssertProblemAbout("maxConcurrentServers", loaded.Problems);
    }

    [Fact]
    public void Parse_TwoServersClaimingOneExtension_GivesItToTheOneDeclaredFirst()
    {
        var loaded = Loaded(
            """
            {
              "servers": {
                "typescript": { "command": "typescript-language-server", "extensions": [".ts", ".tsx"] },
                "deno":       { "command": "deno", "extensions": [".ts"] }
              }
            }
            """);

        Assert.Equal("typescript", loaded.Config.ServerFor("app.ts")!.Language.Value);
        AssertProblemAbout("deno", loaded.Problems);
    }

    [Fact]
    public void Parse_TheLoserOfAnExtensionClash_KeepsTheExtensionsNobodyElseClaimed()
    {
        var loaded = Loaded(
            """
            {
              "servers": {
                "typescript": { "command": "typescript-language-server", "extensions": [".ts"] },
                "deno":       { "command": "deno", "extensions": [".ts", ".tsx"] }
              }
            }
            """);

        var deno = loaded.Config.Servers.Single(s => s.Language.Value == "deno");

        Assert.Equal("typescript", loaded.Config.ServerFor("app.ts")!.Language.Value);
        Assert.Equal("deno", loaded.Config.ServerFor("app.tsx")!.Language.Value);
        // An extension has exactly one owner. Leaving it on both and resolving by list order makes
        // the winner an accident of how the servers happen to be stored.
        Assert.DoesNotContain(".ts", deno.Extensions.Select(e => e.Value));
    }

    [Fact]
    public void Parse_AServerLeftWithNoExtensions_IsNotAServer()
    {
        var loaded = Loaded(
            """
            {
              "servers": {
                "typescript": { "command": "typescript-language-server", "extensions": [".ts"] },
                "deno":       { "command": "deno", "extensions": [".ts"] }
              }
            }
            """);

        Assert.Equal(new[] { "typescript" }, loaded.Config.Servers.Select(s => s.Language.Value));
    }

    [Fact]
    public void Parse_TheSameLanguageTwice_KeepsTheFirstAndSaysSo()
    {
        var loaded = Loaded(
            """
            {
              "servers": {
                "rust": { "command": "rust-analyzer", "extensions": [".rs"] },
                "RUST": { "command": "some-other-thing", "extensions": [".rs"] }
              }
            }
            """);

        var entry = Assert.Single(loaded.Config.Servers);
        Assert.Equal("rust-analyzer", entry.Command);
        Assert.NotEmpty(loaded.Problems);
    }

    static ConfigParse.Loaded Loaded(string json) =>
        Assert.IsType<ConfigParse.Loaded>(LanguageServerConfig.Parse(json));

    static void AssertProblemAbout(string subject, IReadOnlyList<ConfigProblem> problems)
    {
        var matching = problems.Where(p => p.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.NotEmpty(matching);
        Assert.All(matching, problem => Assert.NotEmpty(problem.Message));
    }
}
