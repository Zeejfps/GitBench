using GitBench.Lsp.Configuration;
using GitBench.Lsp.Tests.Fakes;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// Which server answers for a file. Getting this wrong either starts nothing (the feature looks
/// broken) or starts the wrong thing (a Go server asked about Rust).
/// </summary>
public class ServerResolutionTests
{
    static readonly LanguageServerConfig Config = SupervisorHarness.Parsed(
        """
        {
          "servers": {
            "rust":       { "command": "rust-analyzer", "extensions": [".rs"] },
            "typescript": { "command": "typescript-language-server", "extensions": [".ts", ".tsx"] }
          }
        }
        """);

    [Theory]
    [InlineData("src/main.rs", "rust")]
    [InlineData("src/App.tsx", "typescript")]
    [InlineData("src/MAIN.RS", "rust")]
    [InlineData("a b/c (2)/main.rs", "rust")]
    [InlineData("документы/файл.rs", "rust")]
    [InlineData("src/types.d.ts", "typescript")]
    public void ServerFor_AFileWithAClaimedExtension_ResolvesToThatServer(string path, string language)
    {
        Assert.Equal(language, Config.ServerFor(path)!.Language.Value);
    }

    [Theory]
    [InlineData("Makefile")]
    [InlineData("src/notes.md")]
    [InlineData("src/archive.rs.bak")]
    [InlineData("src/trailing.")]
    [InlineData("")]
    public void ServerFor_AFileNoServerClaims_ResolvesToNothing(string path)
    {
        Assert.Null(Config.ServerFor(path));
    }

    [Fact]
    public void ServerFor_AFileThatIsAllExtension_ResolvesToNothing()
    {
        // ".rs" as a whole file name is a file called ".rs", not a Rust file. Treating a dotfile's
        // name as its extension would hand ".gitignore" to whoever claimed ".gitignore".
        Assert.Null(Config.ServerFor(".rs"));
        Assert.Null(Config.ServerFor("src/.rs"));
    }

    [Theory]
    [InlineData(@"C:\code\repo\src\main.rs")]
    [InlineData("/home/zee/repo/src/main.rs")]
    public void ServerFor_EitherPathSeparator_ResolvesTheSame(string path)
    {
        Assert.Equal("rust", Config.ServerFor(path)!.Language.Value);
    }
}
