using System.Diagnostics;
using GitBench.Features.Repos;
using Xunit;

namespace GitBench.Tests;

// The watcher used to build its gitdir as `<repo>/.git`, which is only true for a primary repo. A
// linked worktree and a submodule both have a gitlink *file* there instead, so every path the
// classifier cares about lives somewhere else entirely and those entries classified nothing at all.
//
// The three real-git cases are the point of this file: the gitlink's two forms (git writes an
// absolute path for a worktree and a relative one for a submodule) are exactly what a hand-rolled
// parser gets wrong, so they are pinned against git itself rather than against a fixture.
public sealed class RepoGitDirTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-gitdir-");

    [Fact]
    public void A_primary_repo_resolves_to_its_own_dot_git_directory()
    {
        var primary = NewRepo("primary");

        Assert.True(Directory.Exists(Path.Combine(primary, ".git")), "a primary repo's .git is a directory");
        Assert.Equal(Path.Combine(primary, ".git"), RepoGitDir.Resolve(primary));
    }

    [Fact]
    public void A_linked_worktree_resolves_into_the_primarys_worktrees_directory()
    {
        var primary = NewRepo("primary");
        var worktree = Path.Combine(_dir.Path, "wt");
        Git(primary, "worktree", "add", "-q", worktree, "-b", "side");

        Assert.True(File.Exists(Path.Combine(worktree, ".git")), "a linked worktree's .git is a gitlink file");

        var resolved = RepoGitDir.Resolve(worktree);

        Assert.EndsWith(Path.Combine(".git", "worktrees", "wt"), resolved, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(resolved, "HEAD")), $"'{resolved}' should hold the worktree's own HEAD");
    }

    [Fact]
    public void A_submodule_resolves_into_the_parents_modules_directory()
    {
        var child = NewRepo("child");
        var parent = NewRepo("parent");
        // Modern git refuses file:// submodules unless asked; the alternative is a network fixture.
        // The URL takes forward slashes on every platform, backslashes on none.
        Git(parent, "-c", "protocol.file.allow=always", "submodule", "add", "-q", child.Replace('\\', '/'), "sub");

        var submodule = Path.Combine(parent, "sub");
        Assert.True(File.Exists(Path.Combine(submodule, ".git")), "a submodule's .git is a gitlink file");

        var resolved = RepoGitDir.Resolve(submodule);

        Assert.EndsWith(Path.Combine(".git", "modules", "sub"), resolved, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(resolved, "HEAD")), $"'{resolved}' should hold the submodule's own HEAD");
    }

    // ---- the gitlink's shapes, without git in the loop ----

    [Fact]
    public void A_relative_gitlink_target_is_resolved_against_the_working_tree()
    {
        var repo = Gitlink("gitdir: ../store/modules/sub\n");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_dir.Path, "store", "modules", "sub")),
            RepoGitDir.Resolve(repo));
    }

    [Fact]
    public void An_absolute_gitlink_target_is_taken_as_written()
    {
        var target = Path.Combine(_dir.Path, "store", "worktrees", "wt");
        var repo = Gitlink($"gitdir: {target}\n");

        Assert.Equal(target, RepoGitDir.Resolve(repo));
    }

    // git writes forward slashes into the gitlink on every platform, including Windows, and the
    // watcher compares full paths against this prefix with the platform separator.
    [Fact]
    public void Forward_slashes_are_normalised_to_the_platform_separator()
    {
        var repo = Gitlink("gitdir: ../store/modules/sub\n");

        var resolved = RepoGitDir.Resolve(repo);

        Assert.Equal(resolved.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar), resolved);
    }

    [Theory]
    [InlineData("gitdir:../store/x")]              // no space after the colon
    [InlineData("gitdir:  ../store/x  \n")]        // padded, and no trailing newline is fine either
    [InlineData("gitdir: ../store/x/\n")]          // trailing separator on the target
    public void Loose_spacing_in_the_gitlink_still_resolves(string content)
    {
        var repo = Gitlink(content);

        Assert.Equal(Path.GetFullPath(Path.Combine(_dir.Path, "store", "x")), RepoGitDir.Resolve(repo));
    }

    // ---- fallbacks: never worse than the assumption this replaced ----

    [Theory]
    [InlineData("")]
    [InlineData("not a gitlink at all\n")]
    [InlineData("gitdir:\n")]
    public void An_unrecognised_gitlink_falls_back_to_dot_git(string content)
    {
        var repo = Gitlink(content);

        Assert.Equal(Path.Combine(repo, ".git"), RepoGitDir.Resolve(repo));
    }

    [Fact]
    public void A_path_with_no_dot_git_at_all_falls_back_to_dot_git()
    {
        var empty = Path.Combine(_dir.Path, "empty");
        Directory.CreateDirectory(empty);

        Assert.Equal(Path.Combine(empty, ".git"), RepoGitDir.Resolve(empty));
    }

    // ---- helpers ----

    private string Gitlink(string content)
    {
        var repo = Path.Combine(_dir.Path, "link-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, ".git"), content);
        return repo;
    }

    private string NewRepo(string name)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        Git(path, "config", "user.name", "Test");
        Git(path, "config", "user.email", "test@example.com");
        Git(path, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(path, "a.txt"), "0");
        Git(path, "add", "a.txt");
        Git(path, "commit", "-qm", "base");
        return path;
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
    }

    public void Dispose() => _dir.Dispose();
}
