using System.Diagnostics;
using System.Text.Json;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// <c>find_files</c> exists because the model rarely holds an exact repo-relative path: it has a file
/// name from a diff header, a path one directory off, or a name with two letters swapped. So the
/// tests are about what still resolves — and about the one thing search must not become, which is a
/// way to enumerate the files the reads refuse.
/// </summary>
public sealed class AssistantFindFilesToolTests : IDisposable
{
    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    private readonly string _root;
    private readonly GitService _git;
    private readonly Repo _repo;
    private readonly FindFilesTool _tool;

    public AssistantFindFilesToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-findfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _git = new GitService(new NullActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _root, "test");

        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");

        Track("src/Features/Assistant/AgentCatalog.cs");
        Track("src/Features/Assistant/Tools/ReadFileTool.cs");
        Track("src/Features/Assistant/Tools/RepoFileGuard.cs");
        Track("src/Features/Diff/DiffViewModel.cs");
        Track("src/Git/GitService.cs");
        Track("tests/GitServiceTests.cs");
        Track("README.md");
        Track("build/App.csproj");
        Track(".env");
        Track("config/secrets.json");
        Track("keys/deploy.pem");

        Git("-c", "commit.gpgsign=false", "commit", "-m", "seed");

        _tool = new FindFilesTool(_git, _repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void FindFiles_IsASilentRead()
    {
        Assert.False(_tool.IsWrite);
        JsonDocument.Parse(_tool.JsonSchema).Dispose();
    }

    // The complaint the tool was built for: the model knows the file, not the four directories
    // above it.
    [Fact]
    public void FindFiles_FindsABareNameInASubdirectory()
    {
        Assert.Equal("src/Features/Assistant/AgentCatalog.cs", First("AgentCatalog.cs"));
        Assert.Equal("src/Features/Assistant/AgentCatalog.cs", First("agentcatalog"));
    }

    [Fact]
    public void FindFiles_FindsAPathFragment()
    {
        var matches = Matches("Assistant/Tools");
        Assert.Equal(
            ["src/Features/Assistant/Tools/ReadFileTool.cs", "src/Features/Assistant/Tools/RepoFileGuard.cs"],
            matches.Order(StringComparer.Ordinal));
    }

    // A typo is the other half of the complaint, and a transposition is the common one.
    [Theory]
    [InlineData("RepoFileGaurd.cs")]
    [InlineData("RepoFileGuad.cs")]
    [InlineData("repofileguard")]
    public void FindFiles_SurvivesAMisspelling(string pattern) =>
        Assert.Equal("src/Features/Assistant/Tools/RepoFileGuard.cs", First(pattern));

    [Fact]
    public void FindFiles_MatchesGlobsAcrossDirectories()
    {
        Assert.Equal(["build/App.csproj"], Matches("*.csproj"));
        Assert.Equal(
            ["src/Features/Assistant/Tools/ReadFileTool.cs"],
            Matches("src/*Tool.cs"));
    }

    // An exact path outranks everything that merely contains it, because the caller's next move is
    // to read the first row.
    [Fact]
    public void FindFiles_RanksTheExactPathFirst()
    {
        Assert.Equal("src/Git/GitService.cs", First("src/Git/GitService.cs"));
        Assert.Equal("src/Git/GitService.cs", First("GitService.cs"));
    }

    [Fact]
    public void FindFiles_ReportsTruncation()
    {
        var invocation = Invoke("""{"pattern":"cs","limit":2}""");
        using var json = JsonDocument.Parse(invocation.Content);
        Assert.Equal(2, json.RootElement.GetProperty("matches").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void FindFiles_ReportsNoMatchesRatherThanFailing()
    {
        var invocation = Invoke("""{"pattern":"zzzzzzzzz"}""");
        Assert.False(invocation.IsError, invocation.Content);
        using var json = JsonDocument.Parse(invocation.Content);
        Assert.Equal(0, json.RootElement.GetProperty("matches").GetArrayLength());
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void FindFiles_RequiresAPattern()
    {
        Assert.True(Invoke("""{"pattern":"  "}""").IsError);
        Assert.True(Invoke("{}").IsError);
    }

    // read_file refuses these however they are asked for; search must not hand back a list of them
    // either, or it becomes the enumeration step in front of a refusal.
    [Theory]
    [InlineData(".env")]
    [InlineData("secrets.json")]
    [InlineData("*.pem")]
    [InlineData("deploy")]
    public void FindFiles_NeverReturnsCredentialShapedPaths(string pattern) =>
        Assert.Empty(Matches(pattern));

    [Fact]
    public void FindFiles_CountsOnlyTheSearchableTrackedFiles()
    {
        using var json = JsonDocument.Parse(Invoke("""{"pattern":"README.md"}""").Content);
        Assert.Equal(8, json.RootElement.GetProperty("tracked_files").GetInt32());
    }

    // The refusal is where the model actually lands when it guesses, so the correction belongs in
    // the sentence it reads rather than only in a tool it may not think to call.
    [Fact]
    public void RefusingAnUntrackedPath_SuggestsTheRealOne()
    {
        var resolution = RepoFileGuard.Resolve(_git, _repo, "src/Assistant/AgentCatalog.cs");
        Assert.NotNull(resolution.Refusal);
        Assert.Contains("src/Features/Assistant/AgentCatalog.cs", resolution.Refusal);
    }

    [Fact]
    public void RefusingAPathLikeNothingInTheRepo_SuggestsNothing()
    {
        var resolution = RepoFileGuard.Resolve(_git, _repo, "zzzzzzzzz.txt");
        Assert.NotNull(resolution.Refusal);
        Assert.DoesNotContain("Did you mean", resolution.Refusal);
    }

    private string? First(string pattern) => Matches(pattern).FirstOrDefault();

    private IReadOnlyList<string> Matches(string pattern)
    {
        var invocation = Invoke(JsonSerializer.Serialize(new Dictionary<string, string> { ["pattern"] = pattern }));
        Assert.False(invocation.IsError, invocation.Content);
        using var json = JsonDocument.Parse(invocation.Content);
        return [.. json.RootElement.GetProperty("matches").EnumerateArray().Select(entry => entry.GetString()!)];
    }

    private ToolInvocation Invoke(string args) =>
        _tool.InvokeAsync(AssistantTestJson.Element(args), CancellationToken.None).GetAwaiter().GetResult();

    private void Track(string relative)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "content\n");
        Git("add", "-f", relative);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }
}
