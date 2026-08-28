using GitBench.Pty;

namespace GitBench.Pty.Tests;

public class PtySessionOptionsTests
{
    [Fact]
    public void DefaultsToNoArgumentsNoEnvironmentAndAnEightyByTwentyFourTerminal()
    {
        var options = new PtySessionOptions
        {
            Executable = "bash",
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        Assert.Empty(options.Arguments);
        Assert.Empty(options.Environment);
        Assert.Equal(PtySize.Default, options.Size);
    }
}
