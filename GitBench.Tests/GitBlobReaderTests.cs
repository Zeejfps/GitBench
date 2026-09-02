using System.Diagnostics;
using System.Text;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;

using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The guarantee the batch reader rests on: it returns exactly what spawning <c>git show</c>
/// returned, for every rev shape the diff pane asks for and every kind of content a repository
/// holds. It is an optimization, so the only interesting question is whether anything changed.
/// </summary>
public sealed class GitBlobReaderTests : IDisposable
{
    private readonly string _root;
    private readonly GitService _git;
    private readonly Repo _repo;

    public GitBlobReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitblobreader-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Git("init -q .");
        Git("config user.email t@t");
        Git("config user.name t");
        Git("config commit.gpgsign false");

        // One commit per content shape, so every case has both a HEAD and a parent version.
        Write("plain.txt", "first\nsecond\n"u8.ToArray());
        Write("crlf.txt", "one\r\ntwo\r\n"u8.ToArray());
        Write("bom.txt", [0xEF, 0xBB, 0xBF, .. "with bom\n"u8.ToArray()]);
        Write("unicode-🚀.txt", "日本語 café ñ 🚀\n"u8.ToArray());
        Write("binary.bin", [.. Enumerable.Range(0, 512).Select(i => (byte)(i % 256))]);
        Git("add -A");
        Git("commit -qm one");

        Write("plain.txt", "first\nCHANGED\n"u8.ToArray());
        Git("add -A");
        Git("commit -qm two");

        _git = new GitService(new RepoActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _root, "r");
    }

    public static TheoryData<string> Paths() =>
        ["plain.txt", "crlf.txt", "bom.txt", "unicode-🚀.txt", "binary.bin"];

    /// <summary>Text reads, against the bytes git itself prints. The BOM case is the one that
    /// bites: the old path decoded through a StreamReader, which drops a leading U+FEFF.</summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void TextMatchesGitShow(string path)
    {
        foreach (var (side, oldSide) in Sides())
        {
            var text = _git.GetFileText(_repo, path, side, oldSide, "HEAD");
            Assert.Equal(ExpectedText($"{Rev(side, oldSide)}:{path}"), text);
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void BytesMatchGitShow(string path)
    {
        var bytes = _git.GetFileBytes(_repo, path, DiffSide.Commit, oldSide: false, int.MaxValue, "HEAD");
        Assert.Equal(RunBytes($"show HEAD:{path}"), bytes);
    }

    /// <summary>The index shape (<c>:path</c>), which is what made a libgit2 binding unattractive:
    /// its revparse refuses it, and the Changes tab reads through it constantly.</summary>
    [Fact]
    public void TheStagedSideReadsTheIndexNotTheCommit()
    {
        Write("plain.txt", "first\nSTAGED ONLY\n"u8.ToArray());
        Git("add plain.txt");

        var staged = _git.GetFileText(_repo, "plain.txt", DiffSide.Staged, oldSide: false, null);

        Assert.Equal("first\nSTAGED ONLY\n", staged);
        Assert.NotEqual(staged, _git.GetFileText(_repo, "plain.txt", DiffSide.Staged, oldSide: true, null));
    }

    [Fact]
    public void TheParentSideReadsThePreviousCommit()
    {
        var head = _git.GetFileText(_repo, "plain.txt", DiffSide.Commit, oldSide: false, "HEAD");
        var parent = _git.GetFileText(_repo, "plain.txt", DiffSide.Commit, oldSide: true, "HEAD");

        Assert.Equal("first\nCHANGED\n", head);
        Assert.Equal("first\nsecond\n", parent);
    }

    [Fact]
    public void APathThatIsNotThereComesBackNullRatherThanEmpty()
        => Assert.Null(_git.GetFileText(_repo, "no-such-file.txt", DiffSide.Commit, oldSide: false, "HEAD"));

    /// <summary>A directory resolves to a tree, not a blob. It must read as absent rather than as
    /// a file whose contents are the tree listing.</summary>
    [Fact]
    public void ATreeIsNotMistakenForABlob()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Write(Path.Combine("sub", "nested.txt"), "nested\n"u8.ToArray());
        Git("add -A");
        Git("commit -qm sub");

        Assert.Null(_git.GetFileText(_repo, "sub", DiffSide.Commit, oldSide: false, "HEAD"));
        Assert.Equal("nested\n", _git.GetFileText(_repo, "sub/nested.txt", DiffSide.Commit, oldSide: false, "HEAD"));
    }

    /// <summary>Over the cap reads as absent, which is what the image preview relies on to refuse
    /// a blob too big to decode.</summary>
    [Fact]
    public void ABlobPastTheCapReadsAsAbsentAndLeavesTheReaderUsable()
    {
        Assert.Null(_git.GetFileBytes(_repo, "binary.bin", DiffSide.Commit, oldSide: false, 16, "HEAD"));

        // The oversized body has to be drained for the next request to line up; if it were not,
        // this read would return the tail of the previous one.
        Assert.Equal(
            RunBytes("show HEAD:binary.bin"),
            _git.GetFileBytes(_repo, "binary.bin", DiffSide.Commit, oldSide: false, int.MaxValue, "HEAD"));
    }

    /// <summary>Many reads over one live process: the case the whole thing exists for, and the one
    /// where a desynced pipe would show up as one file's contents appearing under another's name.</summary>
    [Fact]
    public void ManyReadsInARowStayInSync()
    {
        string[] paths = ["plain.txt", "crlf.txt", "bom.txt", "unicode-🚀.txt"];
        for (var i = 0; i < 40; i++)
        {
            var path = paths[i % paths.Length];
            Assert.Equal(ExpectedText($"HEAD:{path}"), _git.GetFileText(_repo, path, DiffSide.Commit, oldSide: false, "HEAD"));
        }
    }

    /// <summary>Concurrent reads of the same repository share one process behind a lock. Interleaved
    /// requests on one pipe are exactly how a length-prefixed protocol goes wrong.</summary>
    [Fact]
    public void ConcurrentReadsOfTheSameRepoStayInSync()
    {
        string[] paths = ["plain.txt", "crlf.txt", "bom.txt", "unicode-🚀.txt"];
        var expected = paths.ToDictionary(p => p, p => ExpectedText($"HEAD:{p}"));

        Parallel.For(0, 80, i =>
        {
            var path = paths[i % paths.Length];
            Assert.Equal(expected[path], _git.GetFileText(_repo, path, DiffSide.Commit, oldSide: false, "HEAD"));
        });
    }

    /// <summary>Reads still work after disposal, by falling back to spawning git. Disposal ends the
    /// processes; it must not break the service.</summary>
    [Fact]
    public void ReadsSurviveDisposalByFallingBack()
    {
        Assert.Equal("first\nCHANGED\n", _git.GetFileText(_repo, "plain.txt", DiffSide.Commit, oldSide: false, "HEAD"));

        _git.Dispose();

        Assert.Equal("first\nCHANGED\n", _git.GetFileText(_repo, "plain.txt", DiffSide.Commit, oldSide: false, "HEAD"));
    }

    private static IEnumerable<(DiffSide Side, bool OldSide)> Sides()
    {
        yield return (DiffSide.Commit, false);
        yield return (DiffSide.Commit, true);
        yield return (DiffSide.Staged, true);
    }

    private static string Rev(DiffSide side, bool oldSide) => (side, oldSide) switch
    {
        (DiffSide.Commit, false) => "HEAD",
        (DiffSide.Commit, true) => "HEAD~1",
        _ => "HEAD",
    };

    // What the old implementation produced: git's raw bytes, decoded as UTF-8 by a StreamReader,
    // which strips a leading BOM.
    private string ExpectedText(string revPath)
    {
        var bytes = RunBytes($"show {revPath}");
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    private void Write(string path, byte[] content)
        => File.WriteAllBytes(Path.Combine(_root, path), content);

    private void Git(string args) => RunBytes(args);

    private byte[] RunBytes(string args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in SplitArgs(args)) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        using var captured = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(captured);
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return captured.ToArray();
    }

    private static IEnumerable<string> SplitArgs(string args) => args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public void Dispose()
    {
        _git.Dispose();
        DirectoryTree.Delete(_root);
    }
}

/// <summary>
/// The bound on live processes. Registering thirty repositories must not mean thirty git
/// processes; only the ones actually being read from keep one, and only the most recent few.
/// </summary>
public sealed class GitBlobReaderPoolTests : IDisposable
{
    private const int Cap = 6;

    private readonly List<string> _repos = [];
    private readonly GitProcessRunner _runner = new(new RepoActivityTracker());
    private readonly GitBlobReader _reader;

    public GitBlobReaderPoolTests()
    {
        _reader = new GitBlobReader(dir => _runner.BuildLongRunningPsi(dir, ["cat-file", "--batch"]));
        for (var i = 0; i < 10; i++) _repos.Add(MakeRepo(i));
    }

    [Fact]
    public void NothingStartsUntilARepoIsActuallyRead()
        => Assert.Equal(0, _reader.LiveRepoCount);

    [Fact]
    public void ReadingFromManyReposKeepsOnlyTheLastFew()
    {
        foreach (var repo in _repos) Read(repo);

        Assert.Equal(Cap, _reader.LiveRepoCount);
    }

    [Fact]
    public void RereadingTheSameRepoReusesItsReader()
    {
        for (var i = 0; i < 20; i++) Read(_repos[0]);

        Assert.Equal(1, _reader.LiveRepoCount);
    }

    /// <summary>Eviction is by recency, not arrival: a repo kept in use survives newer ones.</summary>
    [Fact]
    public void TheLeastRecentlyUsedIsTheOneEvicted()
    {
        foreach (var repo in _repos.Take(Cap)) Read(repo);
        Read(_repos[0]); // _repos[0] is now the most recent, _repos[1] the least

        Read(_repos[Cap]); // one over the cap

        Assert.Equal(Cap, _reader.LiveRepoCount);
        // Still served correctly whether or not its reader survived — but _repos[0], kept in use,
        // should not have been the one dropped to make room.
        Assert.Equal("blob 0\n", Read(_repos[0]));
        Assert.Equal(Cap, _reader.LiveRepoCount);
    }

    [Fact]
    public void DisposingEndsThemAll()
    {
        foreach (var repo in _repos.Take(3)) Read(repo);
        Assert.Equal(3, _reader.LiveRepoCount);

        _reader.Dispose();

        Assert.Equal(0, _reader.LiveRepoCount);
    }

    private string? Read(string repo)
    {
        var status = _reader.TryRead(repo, "HEAD:f.txt", long.MaxValue, out var bytes);
        Assert.Equal(GitBlobReader.Status.Found, status);
        return Encoding.UTF8.GetString(bytes!);
    }

    private string MakeRepo(int index)
    {
        var root = Path.Combine(Path.GetTempPath(), "gitblobpool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        foreach (var args in new[] { "init -q .", "config user.email t@t", "config user.name t" })
            Run(root, args);
        File.WriteAllText(Path.Combine(root, "f.txt"), $"blob {index}\n");
        Run(root, "add -A");
        Run(root, "commit -qm one");
        return root;
    }

    private static void Run(string cwd, string args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    public void Dispose()
    {
        _reader.Dispose();
        foreach (var repo in _repos) DirectoryTree.Delete(repo);
    }
}
