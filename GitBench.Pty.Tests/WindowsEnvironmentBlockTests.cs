using GitBench.Pty.Platforms.Windows;

namespace GitBench.Pty.Tests;

/// <summary>
/// Pins how an overlay layers over the inherited environment and how the result is encoded as the
/// contiguous UTF-16 block CreateProcessW expects: overlay wins, a null value removes, names
/// collate case-insensitively, entries are sorted, and a name that cannot be encoded is rejected
/// at this boundary rather than emitted as a corrupt block.
/// </summary>
public class WindowsEnvironmentBlockTests
{
    [Fact]
    public void EncodesEntriesAsNullTerminatedPairsFollowedByATerminator()
    {
        var block = WindowsEnvironmentBlock.Build([Var("A", "1"), Var("B", "2")], NoOverlay);

        Assert.Equal("A=1\0B=2\0\0", new string(block));
    }

    [Fact]
    public void EncodesAnEmptyEnvironmentAsTheTerminatorAlone()
    {
        var block = WindowsEnvironmentBlock.Build([], NoOverlay);

        Assert.Equal("\0\0", new string(block));
    }

    [Fact]
    public void SortsEntriesByNameCaseInsensitively()
    {
        var block = WindowsEnvironmentBlock.Build(
            [Var("Zulu", "z"), Var("alpha", "a"), Var("Mike", "m")],
            new Dictionary<string, string?> { ["bravo"] = "b" });

        Assert.Equal(["alpha=a", "bravo=b", "Mike=m", "Zulu=z"], Entries(block));
    }

    [Fact]
    public void OverlayReplacesAnInheritedValue()
    {
        var block = WindowsEnvironmentBlock.Build(
            [Var("TERM", "dumb")],
            new Dictionary<string, string?> { ["TERM"] = "xterm-256color" });

        Assert.Equal(["TERM=xterm-256color"], Entries(block));
    }

    [Fact]
    public void OverlayAddsAVariableThatWasNotInherited()
    {
        var block = WindowsEnvironmentBlock.Build(
            [Var("A", "1")],
            new Dictionary<string, string?> { ["COLORTERM"] = "truecolor" });

        Assert.Equal(["A=1", "COLORTERM=truecolor"], Entries(block));
    }

    [Fact]
    public void ANullOverlayValueRemovesAnInheritedVariable()
    {
        var block = WindowsEnvironmentBlock.Build(
            [Var("A", "1"), Var("PROMPT", "$P$G")],
            new Dictionary<string, string?> { ["PROMPT"] = null });

        Assert.Equal(["A=1"], Entries(block));
    }

    [Fact]
    public void ANullOverlayValueRemovesAnInheritedVariableWhoseNameDiffersOnlyByCase()
    {
        var block = WindowsEnvironmentBlock.Build(
            [Var("Path", "c:\\bin")],
            new Dictionary<string, string?> { ["PATH"] = null });

        Assert.Empty(Entries(block));
    }

    [Fact]
    public void RemovingAVariableThatWasNotInheritedIsNotAnError()
    {
        var block = WindowsEnvironmentBlock.Build(
            [Var("A", "1")],
            new Dictionary<string, string?> { ["NEVER_SET"] = null });

        Assert.Equal(["A=1"], Entries(block));
    }

    [Fact]
    public void AnOverlayNameDifferingOnlyByCaseReplacesTheInheritedEntryRatherThanAddingASecond()
    {
        var block = WindowsEnvironmentBlock.Build(
            [Var("Path", "c:\\inherited")],
            new Dictionary<string, string?> { ["path"] = "c:\\overlay" });

        Assert.Equal(["path=c:\\overlay"], Entries(block));
    }

    [Fact]
    public void AnEmptyOverlayValueIsAnEmptyVariableNotARemoval()
    {
        var block = WindowsEnvironmentBlock.Build(
            [Var("A", "1")],
            new Dictionary<string, string?> { ["EMPTY"] = "" });

        Assert.Equal(["A=1", "EMPTY="], Entries(block));
    }

    [Fact]
    public void AnEmptyInheritedValueSurvives()
    {
        var block = WindowsEnvironmentBlock.Build([Var("EMPTY", "")], NoOverlay);

        Assert.Equal(["EMPTY="], Entries(block));
    }

    [Theory]
    [InlineData("")]
    [InlineData("HAS=EQUALS")]
    [InlineData("=C:")]
    [InlineData("HAS\0NULL")]
    public void RejectsAnOverlayNameThatCannotBeEncoded(string name)
    {
        Assert.Throws<ArgumentException>(() => WindowsEnvironmentBlock.Build(
            [],
            new Dictionary<string, string?> { [name] = "value" }));
    }

    [Fact]
    public void RejectsAnOverlayValueContainingANullCharacter()
    {
        Assert.Throws<ArgumentException>(() => WindowsEnvironmentBlock.Build(
            [],
            new Dictionary<string, string?> { ["A"] = "a\0b" }));
    }

    [Fact]
    public void RejectsAnOverlayHoldingTwoNamesThatDifferOnlyByCase()
    {
        Assert.Throws<ArgumentException>(() => WindowsEnvironmentBlock.Build(
            [],
            new Dictionary<string, string?> { ["Path"] = "a", ["PATH"] = "b" }));
    }

    [Fact]
    public void SkipsInheritedEntriesThatCannotBeEncoded()
    {
        var block = WindowsEnvironmentBlock.Build(
            [Var("=C:", "C:\\work"), Var("", "orphan"), Var("A", "a\0b"), Var("KEEP", "1")],
            NoOverlay);

        Assert.Equal(["KEEP=1"], Entries(block));
    }

    [Fact]
    public void TheLastInheritedSpellingOfANameWins()
    {
        var block = WindowsEnvironmentBlock.Build([Var("Path", "first"), Var("PATH", "second")], NoOverlay);

        Assert.Equal(["PATH=second"], Entries(block));
    }

    [Fact]
    public void CapturesTheParentEnvironment()
    {
        var name = "GITBENCH_PTY_" + Guid.NewGuid().ToString("N");
        System.Environment.SetEnvironmentVariable(name, "captured");
        try
        {
            var block = WindowsEnvironmentBlock.Build(
                WindowsEnvironmentBlock.CaptureInherited(),
                NoOverlay);

            Assert.Contains(name + "=captured", Entries(block));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(name, null);
        }
    }

    static readonly IReadOnlyDictionary<string, string?> NoOverlay = new Dictionary<string, string?>();

    static KeyValuePair<string, string> Var(string name, string value) => new(name, value);

    static string[] Entries(char[] block) =>
        new string(block).Split('\0', StringSplitOptions.RemoveEmptyEntries);
}
