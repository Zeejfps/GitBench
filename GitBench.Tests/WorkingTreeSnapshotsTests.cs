using System.Diagnostics;
using GitBench.Features.Pairing;
using Xunit;

namespace GitBench.Tests;

/// <summary>A snapshot pair diffs to exactly what changed between the two captures — edits and new
/// files alike — and taking one never touches the user's index.</summary>
public sealed class WorkingTreeSnapshotsTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-snapshots-");
    private readonly WorkingTreeSnapshots _snapshots = new(new NullActivityTracker());

    public WorkingTreeSnapshotsTests()
    {
        Git("init", "-q");
        Git("config", "user.email", "t@example.com");
        Git("config", "user.name", "T");
        File.WriteAllText(Path.Combine(_dir.Path, "a.txt"), "one\ntwo\n");
        Git("add", "a.txt");
        Git("commit", "-q", "-m", "first");
    }

    public void Dispose() => _dir.Dispose();

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _dir.Path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    private TreeSnapshot Capture() =>
        Assert.IsType<SnapshotResult<TreeSnapshot>.Ok>(_snapshots.Capture(_dir.Path)).Value;

    [Fact]
    public void Diff_HoldsOnlyWhatChangedBetweenTheCaptures()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "a.txt"), "one\nTWO\n");
        var before = Capture();

        File.WriteAllText(Path.Combine(_dir.Path, "a.txt"), "one\nTWO\nthree\n");
        File.WriteAllText(Path.Combine(_dir.Path, "b.txt"), "new\n");
        var after = Capture();

        var diff = Assert.IsType<SnapshotResult<string>.Ok>(_snapshots.Diff(_dir.Path, before, after)).Value;
        Assert.Contains("+three", diff);
        Assert.Contains("b/b.txt", diff);
        Assert.Contains("+new", diff);
        Assert.DoesNotContain("-two", diff);
    }

    [Fact]
    public void NothingChanged_DiffsEmpty()
    {
        var before = Capture();
        var after = Capture();

        Assert.Equal(before, after);
        Assert.Equal(string.Empty, Assert.IsType<SnapshotResult<string>.Ok>(_snapshots.Diff(_dir.Path, before, after)).Value);
    }

    [Fact]
    public void Capture_LeavesTheIndexAlone()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "a.txt"), "changed\n");
        File.WriteAllText(Path.Combine(_dir.Path, "c.txt"), "untracked\n");
        var status = Git("status", "--porcelain");

        Capture();

        Assert.Equal(status, Git("status", "--porcelain"));
        Assert.Equal(string.Empty, Git("diff", "--cached"));
    }

    [Fact]
    public void IgnoredFiles_StayOut()
    {
        File.WriteAllText(Path.Combine(_dir.Path, ".gitignore"), "build/\n");
        var before = Capture();
        Directory.CreateDirectory(Path.Combine(_dir.Path, "build"));
        File.WriteAllText(Path.Combine(_dir.Path, "build", "out.txt"), "artifact\n");
        var after = Capture();

        Assert.Equal(before, after);
    }
}
