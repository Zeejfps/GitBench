using System.Text.Json;
using GitBench.Features.AgentConnections;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Features.Pairing;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The agent pointing at code in the Files pane without a review or a pairing session: a file
/// opened on a declaration or a line, runs of lines lit up with numbered pins.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class EditorPresentationToolsTests : IDisposable
{
    private const string Client =
        "using System;\n"
        + "\n"
        + "namespace Demo;\n"
        + "\n"
        + "public class Client\n"
        + "{\n"
        + "    public int Fetch()\n"
        + "    {\n"
        + "        return 42;\n"
        + "    }\n"
        + "}\n";

    private readonly TempDir _dir = new("gitbench-editor-tools-");
    private readonly FileBrowserViewModel _browser;
    private readonly OneBrowser _browsers;
    private readonly IReadOnlyList<IAssistantTool> _tools;
    private readonly string _clientPath;

    public EditorPresentationToolsTests(CodeIntelFixture fixture)
    {
        var root = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        _clientPath = Path.Combine(root, "src", "Client.cs");
        File.WriteAllText(_clientPath, Client);

        var repo = new Repo(Guid.NewGuid(), root, "repo");
        _browser = new FileBrowserViewModel(
            repo,
            new FileSystemReader(),
            FileBrowserFakes.NoIgnore,
            FileBrowserFakes.EmptyCatalog,
            fixture.Extractor,
            fixture.Colors,
            new QueuedDispatcher(),
            new FileBrowserUiState(),
            _ => { },
            TestDocuments.ForOneRepo(),
            TestDocuments.Discard,
            TestDocuments.KeepEdits);
        _browsers = new OneBrowser(_browser);

        var statePath = Path.Combine(_dir.Path, "repos.json");
        var surface = new AssistantWriteSurface(
            new ImmediateDispatcher(), new MessageBus(), new RepoRegistry(RepoStateStore.Load(statePath), statePath),
            new SilentCommitEditor(), new IdleRemoteOperations(), new TestDocuments.Empty());
        _tools = EditorPresentationTools.CreateAll(
            repo, _browsers, new RepoFilePlaces(repo, FilesOnDisk.Instance, fixture.Extractor), surface);
    }

    public void Dispose()
    {
        _browsers.Dispose();
        _browser.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Spotlight_LightsADeclarationAndALineRange_AndOpensTheFileOnTheFirst()
    {
        var result = Call("editor_spotlight", """
            {"path":"src/Client.cs","spotlights":[{"symbol":"Client.Fetch","note":"the fetch"},{"from":1,"note":"usings"}]}
            """);

        Assert.False(result.IsError, result.Content);
        var shown = Assert.IsType<EditorSpotlights>(_browser.Spotlights.Value);
        Assert.Equal(PathKey.Normalize(_clientPath), shown.Path);
        Assert.Equal(
            [new EditorSpotlight(new LineSpan(7, 10), "the fetch"), new EditorSpotlight(new LineSpan(1, 1), "usings")],
            shown.Runs);
        Assert.Equal(7, _browser.CaretRequest.Value?.At.Line.Value);

        var runs = Parse(result).GetProperty("spotlights");
        Assert.StartsWith("    public int Fetch()", runs[0].GetProperty("text").GetString());
        Assert.Equal("using System;", runs[1].GetProperty("text").GetString());
    }

    [Fact]
    public void Spotlight_PastTheEndOfTheFile_IsRefused_AndLightsNothing()
    {
        var result = Call("editor_spotlight", """{"path":"src/Client.cs","spotlights":[{"from":3},{"from":40,"to":44}]}""");

        Assert.True(result.IsError);
        Assert.Contains("spotlights[1]", result.Content);
        Assert.Null(_browser.Spotlights.Value);
    }

    [Fact]
    public void Spotlight_OnADeclarationThatIsNotThere_NamesTheOnesThatAre()
    {
        var result = Call("editor_spotlight", """{"path":"src/Client.cs","spotlights":[{"symbol":"Client.Store"}]}""");

        Assert.True(result.IsError);
        Assert.Contains("Fetch", result.Content);
    }

    [Fact]
    public void APathOutsideTheRepository_IsRefused()
    {
        var result = Call("editor_show", """{"path":"../elsewhere.cs"}""");

        Assert.True(result.IsError);
        Assert.Contains("outside the repository", result.Content);
    }

    [Fact]
    public void Show_PutsTheCaretOnTheDeclaration_AndQuotesItsLine()
    {
        var result = Call("editor_show", """{"path":"src/Client.cs","symbol":"Fetch"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal(7, Parse(result).GetProperty("line").GetInt32());
        Assert.Equal("    public int Fetch()", Parse(result).GetProperty("line_text").GetString());
        Assert.True(Parse(result).GetProperty("on_screen").GetBoolean());
        Assert.Equal(7, _browser.CaretRequest.Value?.At.Line.Value);
    }

    [Fact]
    public void Clear_TakesTheSpotlightsAway()
    {
        Call("editor_spotlight", """{"path":"src/Client.cs","spotlights":[{"from":1}]}""");

        var result = Call("editor_clear", "{}");

        Assert.False(result.IsError, result.Content);
        Assert.Null(_browser.Spotlights.Value);
    }

    private ToolInvocation Call(string name, string json)
    {
        var tool = _tools.Single(t => t.Name == name);
        var call = tool.InvokeAsync(AssistantTestJson.Element(json), CancellationToken.None);
        Assert.True(call.Wait(TimeSpan.FromSeconds(10)), $"{name} did not finish.");
        return call.Result;
    }

    private static JsonElement Parse(ToolInvocation result) => AssistantTestJson.Element(result.Content);
}
