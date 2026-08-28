using GitBench.Pty;

namespace GitBench.Pty.Tests;

public class PtySessionFactoryTests
{
    readonly PtySessionFactory _factory = new();

    [Fact]
    public void RejectsAMissingExecutable()
    {
        var options = new PtySessionOptions
        {
            Executable = "   ",
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        Assert.Throws<ArgumentException>(() => _factory.Start(options));
    }

    [Fact]
    public void RejectsAWorkingDirectoryThatDoesNotExist()
    {
        var options = new PtySessionOptions
        {
            Executable = "bash",
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "gitbench-pty-" + Guid.NewGuid().ToString("N")),
        };

        Assert.Throws<DirectoryNotFoundException>(() => _factory.Start(options));
    }
}
