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

        var thrown = Assert.Throws<PtySpawnException>(() => _factory.Start(options));

        Assert.Equal(PtySpawnFailure.WorkingDirectoryNotFound, thrown.Failure);
    }

    /// <remarks>
    /// Windows and POSIX agree on what a variable may be called, so this is the factory's to reject
    /// rather than each platform's: unchecked, it fails the spawn on one and silently corrupts the
    /// environment on the other.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("HAS=EQUALS")]
    [InlineData("HAS\u0000NULL")]
    public void RejectsAnEnvironmentNameNoPlatformCanCarry(string name)
    {
        var options = new PtySessionOptions
        {
            Executable = "bash",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Environment = new Dictionary<string, string?> { [name] = "value" },
        };

        Assert.Throws<ArgumentException>(() => _factory.Start(options));
    }

    [Fact]
    public void RejectsAnEnvironmentValueCarryingANull()
    {
        var options = new PtySessionOptions
        {
            Executable = "bash",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Environment = new Dictionary<string, string?> { ["TERM"] = "xterm\u0000256color" },
        };

        Assert.Throws<ArgumentException>(() => _factory.Start(options));
    }
}
