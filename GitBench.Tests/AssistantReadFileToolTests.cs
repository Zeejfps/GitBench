using System.Diagnostics;
using System.Text.Json;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// <c>read_file</c> is the one tool that takes a path, and reads run with no approval card — so what
/// it refuses is the whole of its safety, and every refusal is asserted against a real repository
/// rather than a mocked path resolver.
/// </summary>
public sealed class AssistantReadFileToolTests : IDisposable
{
    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    private readonly string _sandbox;
    private readonly string _root;
    private readonly string _outside;
    private readonly GitService _git;
    private readonly Repo _repo;
    private readonly ReadFileTool _tool;
    private readonly bool _escapeLinkExists;

    public AssistantReadFileToolTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "gitbench-readfile-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "repo");
        _outside = Path.Combine(_sandbox, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "PROD_TOKEN=hunter2\n");
        File.WriteAllText(Path.Combine(_sandbox, "outside.txt"), "PROD_TOKEN=hunter2\n");

        _git = new GitService(new NullActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _root, "test");

        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");

        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllLines(Path.Combine(_root, "src", "app.cs"), Enumerable.Range(1, 40).Select(i => $"line {i}"));
        File.WriteAllText(Path.Combine(_root, ".env"), "API_KEY=hunter2\n");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "logs/\n");
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        File.WriteAllText(Path.Combine(_root, "logs", "app.log"), "committed by mistake\n");
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [0x89, 0x50, 0x00, 0x01, 0x02]);
        // Untracked on purpose: this is the whole category the tracking rule exists to exclude.
        File.WriteAllText(Path.Combine(_root, "scratch.env.txt"), "PROD_TOKEN=hunter2\n");

        _escapeLinkExists = TryLinkOut(Path.Combine(_root, "escape"), _outside);

        Git("add", "src/app.cs", ".env", ".gitignore", "blob.bin");
        Git("add", "-f", "logs/app.log");
        if (_escapeLinkExists) Git("add", "escape");
        Git("-c", "commit.gpgsign=false", "commit", "-m", "seed");

        _tool = new ReadFileTool(_git, _repo);
    }

    // Windows refuses symlinks without the privilege but allows directory junctions, which .NET
    // resolves through the same ResolveLinkTarget the guard walks — so the escape is real either way.
    private static bool TryLinkOut(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
        }
        catch (Exception)
        {
            return false;
        }

        var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        return Directory.Exists(link);
    }

    public void Dispose()
    {
        DirectoryTree.Delete(_sandbox);
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

    // Fixtures the guard will let through: written into the working tree and staged, which is all
    // `git ls-files` needs to call them tracked.
    private void Track(string relative, IEnumerable<string> lines)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllLines(full, lines);
        Git("add", relative);
    }

    private ToolInvocation Read(string args) =>
        _tool.InvokeAsync(AssistantTestJson.Element(args), CancellationToken.None).GetAwaiter().GetResult();

    private ToolInvocation ReadPath(string path) =>
        Read(JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path }));

    [Fact]
    public void ReadFile_IsASilentRead()
    {
        Assert.False(_tool.IsWrite);
        JsonDocument.Parse(_tool.JsonSchema).Dispose();
    }

    [Fact]
    public void ReadFile_ReturnsARangeOfATrackedFile()
    {
        var invocation = Read("""{"path":"src/app.cs","start_line":5,"line_count":3}""");
        Assert.False(invocation.IsError, invocation.Content);

        using var json = JsonDocument.Parse(invocation.Content);
        Assert.Equal("src/app.cs", json.RootElement.GetProperty("path").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("start_line").GetInt32());
        Assert.Equal(7, json.RootElement.GetProperty("end_line").GetInt32());
        Assert.Equal(40, json.RootElement.GetProperty("total_lines").GetInt32());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("line 5\nline 6\nline 7", json.RootElement.GetProperty("content").GetString());
    }

    // The cap is the point: a question about a selection must not be answered with the whole file.
    [Fact]
    public void ReadFile_CapsTheRangeAndSaysSo()
    {
        var invocation = Read("""{"path":"src/app.cs","line_count":10}""");
        using var json = JsonDocument.Parse(invocation.Content);

        Assert.Equal(10, json.RootElement.GetProperty("content").GetString()!.Split('\n').Length);
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());

        // An unbounded ask is clamped rather than honoured; this file is short enough to come back
        // whole, so what the clamp proves is that the argument never sets the ceiling.
        var whole = Read("""{"path":"src/app.cs","line_count":9999}""");
        using var all = JsonDocument.Parse(whole.Content);
        Assert.Equal(40, all.RootElement.GetProperty("end_line").GetInt32());
        Assert.False(all.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(ReadFileTool.MaxLines < 9999);
    }

    /// <summary>
    /// The size ceiling must shorten the range, never perforate it. A file whose fifth line is far
    /// over the ceiling used to have that line skipped and the shorter lines after it collected in
    /// its place, so <c>content</c> jumped from line 4 to line 6 while <c>end_line</c> counted as
    /// though it had not — every number the model then quoted was wrong by the size of the hole.
    /// </summary>
    [Fact]
    public void ReadFile_StopsAtAnOversizeLineRatherThanReadingAroundIt()
    {
        Track("src/wide.cs", Enumerable.Range(1, 20).Select(i => i == 5 ? new string('x', 200_000) : $"line {i}"));

        var invocation = Read("""{"path":"src/wide.cs","start_line":1,"line_count":20}""");
        Assert.False(invocation.IsError, invocation.Content);

        using var json = JsonDocument.Parse(invocation.Content);
        Assert.Equal("line 1\nline 2\nline 3\nline 4", json.RootElement.GetProperty("content").GetString());
        Assert.Equal(4, json.RootElement.GetProperty("end_line").GetInt32());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(20, json.RootElement.GetProperty("total_lines").GetInt32());

        // Asking for the oversize line itself is the one case with no contiguous answer at all, and
        // it comes back as a sentence rather than as empty content the model would have to guess at.
        var onTheLine = Read("""{"path":"src/wide.cs","start_line":5,"line_count":20}""");
        Assert.True(onTheLine.IsError);
        Assert.Contains("Line 5", onTheLine.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case that made this urgent: not a minified bundle, just ordinary source of mixed line
    /// lengths long enough to reach the byte ceiling inside a 1200-line ask. Every line carries its
    /// own number, so a gap anywhere in the returned range is visible.
    /// </summary>
    [Fact]
    public void ReadFile_ReturnsNoGapsInOrdinarySourceThatHitsTheByteCeiling()
    {
        Track("src/mixed.cs", Enumerable.Range(1, 1200).Select(MixedLine));

        var invocation = Read("""{"path":"src/mixed.cs","start_line":1,"line_count":1200}""");
        Assert.False(invocation.IsError, invocation.Content);

        using var json = JsonDocument.Parse(invocation.Content);
        var content = json.RootElement.GetProperty("content").GetString()!.Split('\n');
        var end = json.RootElement.GetProperty("end_line").GetInt32();

        // If the ceiling stopped being reachable this way the test would pass while proving nothing.
        Assert.True(content.Length < 1200, $"the byte ceiling was never reached ({content.Length} lines)");
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(content.Length, end);

        for (var i = 0; i < content.Length; i++)
            Assert.Equal(MixedLine(i + 1), content[i]);
    }

    // Deterministic ordinary-source shape: 40-120 characters, one line in ten at 200-400.
    private static string MixedLine(int number)
    {
        var width = number % 10 == 0 ? 200 + number % 200 : 40 + number % 80;
        return $"{number:D4}: " + new string('.', width);
    }

    /// <summary>
    /// <c>truncated</c> means one thing: there is file left after <c>end_line</c>. It is not a
    /// property of how the range was asked for.
    /// </summary>
    [Fact]
    public void Truncated_IsTrueExactlyWhenTheRangeStopsShortOfTheEnd()
    {
        var toTheEnd = Read("""{"path":"src/app.cs","start_line":38,"line_count":3}""");
        using var tail = JsonDocument.Parse(toTheEnd.Content);
        Assert.Equal(40, tail.RootElement.GetProperty("end_line").GetInt32());
        Assert.False(tail.RootElement.GetProperty("truncated").GetBoolean());

        var oneShort = Read("""{"path":"src/app.cs","start_line":38,"line_count":2}""");
        using var stops = JsonDocument.Parse(oneShort.Content);
        Assert.Equal(39, stops.RootElement.GetProperty("end_line").GetInt32());
        Assert.True(stops.RootElement.GetProperty("truncated").GetBoolean());

        // Past the end there is nothing to have been cut off, so nothing is claimed to be.
        var beyond = Read("""{"path":"src/app.cs","start_line":100,"line_count":10}""");
        using var empty = JsonDocument.Parse(beyond.Content);
        Assert.Equal("", empty.RootElement.GetProperty("content").GetString());
        Assert.False(empty.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(40, empty.RootElement.GetProperty("total_lines").GetInt32());
    }

    // Past the scan ceiling total_lines is a floor rather than a count, and it must be the ceiling
    // itself: reporting one line more than was ever scanned made it a number that matched nothing.
    [Fact]
    public void ReadFile_StopsCountingAtTheScanCeiling()
    {
        Track("src/vast.cs", Enumerable.Range(1, ReadFileTool.MaxScannedLines + 500).Select(i => $"line {i}"));

        var invocation = Read("""{"path":"src/vast.cs","start_line":1,"line_count":3}""");
        using var json = JsonDocument.Parse(invocation.Content);

        Assert.Equal("line 1\nline 2\nline 3", json.RootElement.GetProperty("content").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("end_line").GetInt32());
        Assert.Equal(ReadFileTool.MaxScannedLines, json.RootElement.GetProperty("total_lines").GetInt32());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void ReadFile_RefusesABinaryBlob()
    {
        var invocation = ReadPath("blob.bin");
        Assert.True(invocation.IsError);
        Assert.Contains("binary", invocation.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The table of ways out. Each one comes back as an <c>is_error</c> result the model can read and
    /// adapt to — never as a thrown turn, and never as content.
    /// </summary>
    [Fact]
    public void ReadFile_RefusesEveryWayOutOfTheRepository()
    {
        var attempts = new List<(string Path, string Expect)>
        {
            ("../outside.txt", "outside the repository"),
            ("src/../../outside.txt", "outside the repository"),
            (Path.Combine(_sandbox, "outside.txt"), "outside the repository"),
            ("/etc/passwd", "outside the repository"),
            ("scratch.env.txt", "does not track"),
            ("logs/app.log", "ignored"),
            (".env", "credentials file"),
            ("src/id_rsa", "credentials file"),
            ("config/service.pem", "credentials file"),
        };

        // A tracked file that physically lives outside the checkout: git says yes, and only the
        // link walk says no. Skipping it silently would leave the guard's hardest case unasserted.
        Assert.True(_escapeLinkExists, "the escaping link could not be created, so its refusal is untested");
        attempts.Add(("escape/secret.txt", "outside the repository"));

        foreach (var (path, expect) in attempts)
        {
            var invocation = ReadPath(path);
            Assert.True(invocation.IsError, $"'{path}' was allowed: {invocation.Content}");
            Assert.Contains(expect, invocation.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hunter2", invocation.Content, StringComparison.Ordinal);
        }
    }

    // The escaping file is genuinely there through the link, so a tool that opened the path it was
    // given would have handed over its contents. Only the link walk stops it.
    [Fact]
    public void TheEscapingFile_IsReachableOnDiskAndStillRefused()
    {
        Assert.True(_escapeLinkExists);
        Assert.True(File.Exists(Path.Combine(_root, "escape", "secret.txt")));

        var invocation = ReadPath("escape/secret.txt");
        Assert.True(invocation.IsError);
        Assert.Contains("outside the repository", invocation.Content, StringComparison.OrdinalIgnoreCase);
    }

    // A tracked file the ignore rules also match: tracking wins in git, and here it must not.
    [Fact]
    public void AnIgnoredFile_IsRefusedEvenThoughGitTracksIt()
    {
        Assert.True(_git.IsPathTracked(_repo, "logs/app.log"));
        Assert.True(_git.IsPathIgnored(_repo, "logs/app.log"));
        Assert.True(ReadPath("logs/app.log").IsError);
    }
}
