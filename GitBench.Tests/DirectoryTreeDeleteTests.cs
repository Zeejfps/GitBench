using System.Diagnostics;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

// DirectoryTree exists for what a real working tree holds on Windows and git's own recursive
// delete gets wrong: junctions (which it tries to unlink as files), read-only files, and files
// something else has open for a moment.
public sealed class DirectoryTreeDeleteTests : IDisposable
{
    private readonly string _root;

    public DirectoryTreeDeleteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-rmtree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeTree(string name)
    {
        var tree = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(tree, "nested", "deeper"));
        File.WriteAllText(Path.Combine(tree, "top.txt"), "top");
        File.WriteAllText(Path.Combine(tree, "nested", "deeper", "leaf.txt"), "leaf");
        return tree;
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"mklink /J failed: {stderr}");
    }

    [Fact]
    public void DeletesAPlainTree()
    {
        var tree = MakeTree("plain");

        Assert.Null(DirectoryTree.Delete(tree));
        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void AMissingPathIsNotAFailure()
        => Assert.Null(DirectoryTree.Delete(Path.Combine(_root, "never-existed")));

    [Fact]
    public void DeletesReadOnlyFiles()
    {
        var tree = MakeTree("readonly");
        var file = Path.Combine(tree, "nested", "deeper", "leaf.txt");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        Assert.Null(DirectoryTree.Delete(tree));
        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void RemovesJunctionsWithoutFollowingThem()
    {
        if (!OperatingSystem.IsWindows()) return;

        var target = Path.Combine(_root, "outside");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");

        var tree = MakeTree("junctions");
        Junction(Path.Combine(tree, "nested", "link"), target);

        Assert.Null(DirectoryTree.Delete(tree));
        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    // The shape git chokes on: a junction whose target no longer exists.
    [Fact]
    public void RemovesBrokenJunctions()
    {
        if (!OperatingSystem.IsWindows()) return;

        var target = Path.Combine(_root, "vanishing");
        Directory.CreateDirectory(target);

        var tree = MakeTree("broken-junctions");
        Junction(Path.Combine(tree, "nested", "link"), target);
        Directory.Delete(target);

        Assert.Null(DirectoryTree.Delete(tree));
        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void ReportsTheLeftoversWhenSomethingHoldsAFileOpen()
    {
        if (!OperatingSystem.IsWindows()) return;

        var tree = MakeTree("locked");
        using var handle = new FileStream(
            Path.Combine(tree, "nested", "held.bin"), FileMode.Create, FileAccess.Write, FileShare.None);

        var leftovers = DirectoryTree.Delete(tree);

        Assert.NotNull(leftovers);
        Assert.Equal(tree, leftovers!.Path);
        Assert.NotEmpty(leftovers.Reason);
        Assert.True(Directory.Exists(tree));
    }
}
