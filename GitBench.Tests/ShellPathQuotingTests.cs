using GitBench.Features.Terminal;
using Xunit;

namespace GitBench.Tests;

public class ShellPathQuotingTests
{
    [Fact]
    public void APlainPathIsQuotedForEachShell()
    {
        const string path = "/Users/dev/my repo";

        Assert.Equal("cd -- '/Users/dev/my repo'",
            ShellPathQuoting.ChangeDirectoryCommand(path, ShellFamily.Posix));
        Assert.Equal("Set-Location -LiteralPath '/Users/dev/my repo'",
            ShellPathQuoting.ChangeDirectoryCommand(path, ShellFamily.PowerShell));
        Assert.Equal("cd /d \"/Users/dev/my repo\"",
            ShellPathQuoting.ChangeDirectoryCommand(path, ShellFamily.CommandProcessor));
    }

    [Fact]
    public void APosixQuoteIsClosedEscapedAndReopened()
    {
        Assert.Equal(@"cd -- '/tmp/it'\''s here'",
            ShellPathQuoting.ChangeDirectoryCommand("/tmp/it's here", ShellFamily.Posix));
    }

    [Fact]
    public void APosixPathCannotEscapeItsQuotesAndRunSomething()
    {
        var command = ShellPathQuoting.ChangeDirectoryCommand("/tmp/'; touch pwned; '", ShellFamily.Posix)!;

        Assert.StartsWith("cd -- '", command, StringComparison.Ordinal);
        Assert.EndsWith("'", command, StringComparison.Ordinal);
        Assert.DoesNotContain("; touch pwned; '\"", command, StringComparison.Ordinal);
        Assert.Equal(@"cd -- '/tmp/'\''; touch pwned; '\'''", command);
    }

    [Fact]
    public void APowerShellQuoteIsDoubled()
    {
        Assert.Equal("Set-Location -LiteralPath '/tmp/it''s here'",
            ShellPathQuoting.ChangeDirectoryCommand("/tmp/it's here", ShellFamily.PowerShell));
    }

    [Fact]
    public void APowerShellLiteralDoesNotExpandAVariable()
    {
        Assert.Equal("Set-Location -LiteralPath '/tmp/$(Get-Date)'",
            ShellPathQuoting.ChangeDirectoryCommand("/tmp/$(Get-Date)", ShellFamily.PowerShell));
    }

    [Theory]
    [InlineData("C:\\repos\\%TEMP%")]
    [InlineData("C:\\repos\\say \"hi\"")]
    [InlineData("C:\\repos\\bang!")]
    public void TheCommandProcessorRefusesWhatItCannotQuote(string path)
    {
        Assert.Null(ShellPathQuoting.ChangeDirectoryCommand(path, ShellFamily.CommandProcessor));
    }

    [Fact]
    public void APathCarryingANewlineIsRefusedByEveryShell()
    {
        foreach (var family in Families)
            Assert.Null(ShellPathQuoting.ChangeDirectoryCommand("/tmp/one\nwhoami", family));
    }

    [Fact]
    public void AnEmptyPathIsRefusedByEveryShell()
    {
        foreach (var family in Families)
            Assert.Null(ShellPathQuoting.ChangeDirectoryCommand("", family));
    }

    private static readonly ShellFamily[] Families =
        [ShellFamily.Posix, ShellFamily.PowerShell, ShellFamily.CommandProcessor];
}
