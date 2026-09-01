using System.Diagnostics;
using System.Text;
using GitBench.Git;

using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The parser that replaced <c>git config</c> on the identity path. Its whole justification is
/// that it answers what git answered, so most of this is differential: ask both, compare.
/// </summary>
public class GitConfigFileTests
{
    private static readonly string Scratch =
        Path.Combine(Path.GetTempPath(), "gitconfig-" + Guid.NewGuid().ToString("N")[..8]);

    // Every repository on this machine, including the linked worktrees and submodules whose config
    // lives somewhere other than their own git directory — the case a naive `<repo>/.git/config`
    // would get wrong.
    public static TheoryData<string> RealRepos()
    {
        var data = new TheoryData<string>();
        var roots = new List<string> { Directory.GetCurrentDirectory() };
        var testRepos = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "gitbench-test-repos");
        if (Directory.Exists(testRepos)) roots.AddRange(Directory.GetDirectories(testRepos));

        foreach (var root in roots)
        {
            if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
                data.Add(root);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RealRepos))]
    public void IdentityMatchesGitConfig(string repo)
    {
        var config = GitConfigFile.ForRepo(repo);

        Assert.Equal(GitConfig(repo, "user.name"), config.Get("user", null, "name"));
        Assert.Equal(GitConfig(repo, "user.email"), config.Get("user", null, "email"));
    }

    [Theory]
    [MemberData(nameof(RealRepos))]
    public void RemoteNamesMatchGitRemote(string repo)
    {
        var expected = Run(repo, "remote")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, GitConfigFile.ForRepo(repo).Subsections("remote"));
    }

    [Theory]
    [MemberData(nameof(RealRepos))]
    public void RemoteUrlsMatchGitRemoteGetUrl(string repo)
    {
        var config = GitConfigFile.ForRepo(repo);
        foreach (var remote in config.Subsections("remote"))
            Assert.Equal(GitConfig(repo, $"remote.{remote}.url"), config.Get("remote", remote, "url"));
    }

    [Fact]
    public void AnAbsentFileReadsAsUnsetRatherThanThrowing()
        => Assert.Null(GitConfigFile.Read(Path.Combine(Scratch, "nope", "config")).Get("user", null, "name"));

    /// <summary>Differential on the fiddly parts of the format — comments, quoting, escapes, line
    /// continuation, last-one-wins. git supplies the expected answer, so these pin real behavior
    /// rather than my reading of the documentation.</summary>
    [Theory]
    [InlineData("[USER]\n\tNAME = Ada\n")]                              // names are case-insensitive
    [InlineData("[user]\n\tname = Ada Lovelace   # the first\n")]       // inline comment
    [InlineData("[user]\n\tname = Ada ; semicolon comment\n")]
    [InlineData("[user]\n\tname = \"Ada # Lovelace\"\n")]              // quotes protect a #
    [InlineData("[user]\n\tname = \"  padded  \"\n")]                  // and protect whitespace
    [InlineData("[user]\n\tname =    spaced   out   \n")]               // internal vs trailing
    [InlineData("[user]\n\tname = \"a\\\"b\"\n")]                     // escaped quote
    [InlineData("[user]\n\tname = a\\\\b\n")]                          // escaped backslash
    [InlineData("[user]\n\tname = a\\tb\n")]                           // tab escape
    [InlineData("[user]\n\tname = Ada \\\n Lovelace\n")]               // line continuation
    [InlineData("[user]\n\tname = First\n\tname = Second\n")]          // last one wins
    [InlineData("[user]\n[core]\n\tbare = false\n[user]\n\tname = Ada\n")] // reopened section
    [InlineData("[user]\n\tname =\n")]                                  // set but empty
    public void ValueParsingMatchesGitsRules(string content)
    {
        Directory.CreateDirectory(Scratch);
        var path = Path.Combine(Scratch, Guid.NewGuid().ToString("N")[..8] + ".config");
        File.WriteAllText(path, content);

        var viaGit = Run(Scratch, $"config --file {path} --get user.name");
        var expected = string.IsNullOrEmpty(viaGit) ? null : viaGit.TrimEnd('\n');

        Assert.Equal(expected, GitConfigFile.Read(path).Get("user", null, "name"));
    }

    [Fact]
    public void SubsectionNamesKeepTheirCaseAndMayContainDots()
    {
        var config = WriteAndRead(
            "[remote \"Origin.Fork\"]\n\turl = git@example.com:a/b.git\n" +
            "[remote \"origin\"]\n\turl = git@example.com:c/d.git\n");

        Assert.Equal(["Origin.Fork", "origin"], config.Subsections("remote"));
        Assert.Equal("git@example.com:a/b.git", config.Get("remote", "Origin.Fork", "url"));
        Assert.Null(config.Get("remote", "origin.fork", "url"));
    }

    [Fact]
    public void ABareKeyIsTrue()
        => Assert.Equal("true", WriteAndRead("[extensions]\n\tworktreeConfig\n").Get("extensions", null, "worktreeconfig"));

    /// <summary>A linked worktree's config lives in the repository's common git directory, not in
    /// its own — reading <c>&lt;worktree&gt;/.git/config</c> would find nothing.</summary>
    [Fact]
    public void ALinkedWorktreeReadsTheCommonConfig()
    {
        var main = Path.Combine(Scratch, "main-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(main);
        Run(main, "init -q .");
        Run(main, "config user.email wt@example.com");
        Run(main, "config user.name Worktree Owner");
        File.WriteAllText(Path.Combine(main, "f.txt"), "x\n");
        Run(main, "add -A");
        Run(main, "commit -qm one");

        var linked = main + "-linked";
        Run(main, $"worktree add -q {linked} -b side");
        Assert.True(File.Exists(Path.Combine(linked, ".git")), "worktree should use a gitlink file");

        Assert.Equal("wt@example.com", GitConfigFile.ForRepo(linked).Get("user", null, "email"));
        Assert.Equal(GitConfig(linked, "user.email"), GitConfigFile.ForRepo(linked).Get("user", null, "email"));
    }

    private static GitConfigFile WriteAndRead(string content)
    {
        Directory.CreateDirectory(Scratch);
        var path = Path.Combine(Scratch, Guid.NewGuid().ToString("N")[..8] + ".config");
        File.WriteAllText(path, content);
        return GitConfigFile.Read(path);
    }

    // `--local --get` exits 1 for an unset key; that is "not configured", not a failure.
    private static string? GitConfig(string repo, string key)
    {
        var value = Run(repo, $"config --local --get {key}");
        return string.IsNullOrEmpty(value) ? null : value.TrimEnd('\n');
    }

    private static string Run(string cwd, string args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : string.Empty;
    }
}
