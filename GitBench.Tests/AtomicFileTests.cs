using System.Text;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

/// <summary>The write every store in the app goes through: exact bytes, and the mode the file already had.</summary>
public class AtomicFileTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-atomicfile-");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void AByteWriteProducesExactlyThoseBytes()
    {
        byte[] contents = [.. Encoding.UTF8.GetPreamble(), .. "a\r\nb"u8];
        var path = Path.Combine(_dir.Path, "sample.txt");

        AtomicFile.WriteAllBytes(path, contents);

        Assert.Equal(contents, File.ReadAllBytes(path));
    }

    [Fact]
    public void AByteWriteReplacesWhatWasThereAndLeavesNoTempBehind()
    {
        var path = Path.Combine(_dir.Path, "sample.txt");
        AtomicFile.WriteAllBytes(path, "old and longer"u8);

        AtomicFile.WriteAllBytes(path, "new"u8);

        Assert.Equal("new"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_dir.Path).Where(AtomicFile.IsStagingPath));
    }

    [Fact]
    public void AStagingFileIsToldApartFromAFileSomeoneMeantToKeep()
    {
        Assert.False(AtomicFile.IsStagingPath("/repo/notes.tmp"));
        Assert.False(AtomicFile.IsStagingPath("/repo/notes.txt"));
        Assert.True(AtomicFile.IsStagingPath("/repo/notes.txt.1234-1.gitbench.tmp"));
    }

    [Fact]
    public void AByteWriteCreatesTheDirectoryItWasPointedAt()
    {
        var path = Path.Combine(_dir.Path, "nested", "deeper", "sample.txt");

        AtomicFile.WriteAllBytes(path, "hi"u8);

        Assert.Equal("hi"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void AnExistingFilesModeSurvivesTheReplace()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_dir.Path, "hook.sh");
        File.WriteAllText(path, "#!/bin/sh\n");
        const UnixFileMode executable =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, executable);

        AtomicFile.WriteAllBytes(path, "#!/bin/sh\necho hi\n"u8);

        Assert.Equal(executable, File.GetUnixFileMode(path));
    }

    [Fact]
    public void ATextWriteStillWritesTextAndStillPreservesTheMode()
    {
        var path = Path.Combine(_dir.Path, "state.json");
        AtomicFile.WriteAllText(path, "{}");

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        AtomicFile.WriteAllText(path, "{\"a\":1}");

        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
    }
}
