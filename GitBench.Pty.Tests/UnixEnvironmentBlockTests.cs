using System.Text;
using GitBench.Pty.Platforms.Unix;

namespace GitBench.Pty.Tests;

/// <summary>
/// Pins how an overlay layers over the inherited environment on POSIX, where a name is a byte string
/// and nothing collates it: overlay wins, a null value removes, and two names that differ in case are
/// two variables rather than one.
/// </summary>
/// <remarks>
/// The mirror of <see cref="WindowsEnvironmentBlockTests"/>, and deliberately the opposite of it
/// wherever the two platforms disagree — every case-related assertion here is the inverse of the
/// Windows one, and that is the whole reason both files exist. Like that one it runs on every host,
/// because none of this makes a system call.
/// </remarks>
public class UnixEnvironmentBlockTests
{
    [Fact]
    public void EncodesEachEntryAsANullTerminatedNameValuePair()
    {
        var block = UnixEnvironmentBlock.Build([Var("A", "1"), Var("B", "2")], NoOverlay);

        Assert.Equal(["A=1", "B=2"], Entries(block));
    }

    [Fact]
    public void EncodesAnEmptyEnvironmentAsNoEntriesAtAll()
    {
        var block = UnixEnvironmentBlock.Build([], NoOverlay);

        Assert.Empty(block);
    }

    [Fact]
    public void OrdersEntriesByName()
    {
        var block = UnixEnvironmentBlock.Build(
            [Var("Zulu", "z"), Var("alpha", "a"), Var("Mike", "m")],
            new Dictionary<string, string?> { ["bravo"] = "b" });

        Assert.Equal(["Mike=m", "Zulu=z", "alpha=a", "bravo=b"], Entries(block));
    }

    [Fact]
    public void OverlayReplacesAnInheritedValue()
    {
        var block = UnixEnvironmentBlock.Build(
            [Var("TERM", "dumb")],
            new Dictionary<string, string?> { ["TERM"] = "xterm-256color" });

        Assert.Equal(["TERM=xterm-256color"], Entries(block));
    }

    [Fact]
    public void OverlayAddsAVariableThatWasNotInherited()
    {
        var block = UnixEnvironmentBlock.Build(
            [Var("A", "1")],
            new Dictionary<string, string?> { ["COLORTERM"] = "truecolor" });

        Assert.Equal(["A=1", "COLORTERM=truecolor"], Entries(block));
    }

    [Fact]
    public void ANullOverlayValueRemovesAnInheritedVariable()
    {
        var block = UnixEnvironmentBlock.Build(
            [Var("A", "1"), Var("PS1", "$ ")],
            new Dictionary<string, string?> { ["PS1"] = null });

        Assert.Equal(["A=1"], Entries(block));
    }

    /// <remarks>The Windows block removes here, because Windows says the two names are one.</remarks>
    [Fact]
    public void ANullOverlayValueLeavesAnInheritedVariableWhoseNameDiffersOnlyByCaseAlone()
    {
        var block = UnixEnvironmentBlock.Build(
            [Var("Path", "/inherited")],
            new Dictionary<string, string?> { ["PATH"] = null });

        Assert.Equal(["Path=/inherited"], Entries(block));
    }

    [Fact]
    public void RemovingAVariableThatWasNeverInheritedIsNotAnError()
    {
        var block = UnixEnvironmentBlock.Build(
            [Var("A", "1")],
            new Dictionary<string, string?> { ["NEVER_SET"] = null });

        Assert.Equal(["A=1"], Entries(block));
    }

    /// <remarks>The Windows block replaces here, for the same reason.</remarks>
    [Fact]
    public void AnOverlayNameDifferingOnlyByCaseAddsASecondVariableRatherThanReplacing()
    {
        var block = UnixEnvironmentBlock.Build(
            [Var("Path", "/inherited")],
            new Dictionary<string, string?> { ["PATH"] = "/overlay" });

        Assert.Equal(["PATH=/overlay", "Path=/inherited"], Entries(block));
    }

    /// <remarks>
    /// The Windows block throws for this, because on Windows it is one name set twice. Here there is
    /// nothing to reject: the two are different variables and a plain dictionary holds both.
    /// </remarks>
    [Fact]
    public void AnOverlayHoldingTwoNamesThatDifferOnlyByCaseKeepsBoth()
    {
        var block = UnixEnvironmentBlock.Build(
            [],
            new Dictionary<string, string?> { ["Path"] = "/a", ["PATH"] = "/b" });

        Assert.Equal(["PATH=/b", "Path=/a"], Entries(block));
    }

    [Fact]
    public void AnEmptyOverlayValueIsAnEmptyVariableNotARemoval()
    {
        var block = UnixEnvironmentBlock.Build(
            [Var("A", "1")],
            new Dictionary<string, string?> { ["EMPTY"] = "" });

        Assert.Equal(["A=1", "EMPTY="], Entries(block));
    }

    [Fact]
    public void AnEmptyInheritedValueSurvives()
    {
        var block = UnixEnvironmentBlock.Build([Var("EMPTY", "")], NoOverlay);

        Assert.Equal(["EMPTY="], Entries(block));
    }

    /// <remarks>
    /// Only the first equals sign separates a name from a value, so a value full of them is one value
    /// and not a malformed entry.
    /// </remarks>
    [Fact]
    public void AValueContainingEqualsSignsSurvivesWhole()
    {
        var block = UnixEnvironmentBlock.Build([Var("OPTS", "a=b=c")], NoOverlay);

        Assert.Equal(["OPTS=a=b=c"], Entries(block));
    }

    [Fact]
    public void EncodesNonAsciiNamesAndValuesAsUtf8()
    {
        var block = UnixEnvironmentBlock.Build(
            [], new Dictionary<string, string?> { ["CAFÉ"] = "naïve-日本語" });

        var entry = Assert.Single(block);

        Assert.Equal(0, entry[^1]);
        Assert.Equal(Encoding.UTF8.GetBytes("CAFÉ=naïve-日本語"), entry[..^1]);
    }

    /// <remarks>
    /// A lone surrogate cannot be encoded and the default UTF-8 fallback substitutes a replacement
    /// character for it. What matters is only that the substitution cannot forge structure: no
    /// embedded null to end the entry early, and no second equals sign to move the name boundary.
    /// </remarks>
    [Fact]
    public void EncodesAnUnpairedSurrogateWithoutProducingANullOrAnExtraSeparator()
    {
        var block = UnixEnvironmentBlock.Build(
            [], new Dictionary<string, string?> { ["A"] = "before\ud800after" });

        var entry = Assert.Single(block);
        var body = entry[..^1];

        Assert.Equal(0, entry[^1]);
        Assert.DoesNotContain((byte)0, body);
        Assert.Equal(1, body.Count(b => b == (byte)'='));
    }

    [Theory]
    [InlineData("")]
    [InlineData("HAS=EQUALS")]
    [InlineData("=C:")]
    [InlineData("HAS\u0000NULL")]
    public void RejectsAnOverlayNameThatCannotBeEncoded(string name)
    {
        Assert.Throws<ArgumentException>(
            () => UnixEnvironmentBlock.Build([], new Dictionary<string, string?> { [name] = "value" }));
    }

    [Fact]
    public void RejectsAnOverlayValueContainingANullCharacter()
    {
        Assert.Throws<ArgumentException>(
            () => UnixEnvironmentBlock.Build([], new Dictionary<string, string?> { ["A"] = "a\u0000b" }));
    }

    /// <remarks>
    /// The overlay is caller input and a bad name there is a bug worth reporting. The inherited set is
    /// ambient — whatever this process happens to be carrying, including the per-drive pseudo-variables
    /// Windows puts there — so an entry that cannot be encoded is dropped rather than made to fail a
    /// spawn the caller did nothing wrong in.
    /// </remarks>
    [Fact]
    public void SkipsInheritedEntriesThatCannotBeEncoded()
    {
        var block = UnixEnvironmentBlock.Build(
            [Var("=C:", "C:\\work"), Var("", "orphan"), Var("A", "a\u0000b"), Var("KEEP", "1")],
            NoOverlay);

        Assert.Equal(["KEEP=1"], Entries(block));
    }

    [Fact]
    public void TheLastInheritedSpellingOfTheSameNameWins()
    {
        var block = UnixEnvironmentBlock.Build(
            [Var("PATH", "/first"), Var("PATH", "/second")], NoOverlay);

        Assert.Equal(["PATH=/second"], Entries(block));
    }

    /// <remarks>
    /// The kernel cares only that a name carries no equals sign and no null. Names a style guide would
    /// reject are still names, and rejecting them here would refuse environments that work.
    /// </remarks>
    [Theory]
    [InlineData("9LIVES")]
    [InlineData("has space")]
    [InlineData("_")]
    public void AcceptsANameThatIsLegalToTheKernelIfNotToTheStyleGuide(string name)
    {
        var block = UnixEnvironmentBlock.Build([], new Dictionary<string, string?> { [name] = "v" });

        Assert.Equal([$"{name}=v"], Entries(block));
    }

    [Fact]
    public void CapturesTheParentEnvironment()
    {
        var name = "GITBENCH_PTY_" + Guid.NewGuid().ToString("N");
        System.Environment.SetEnvironmentVariable(name, "captured");

        try
        {
            var block = UnixEnvironmentBlock.Build(UnixEnvironmentBlock.CaptureInherited(), NoOverlay);

            Assert.Contains($"{name}=captured", Entries(block));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(name, null);
        }
    }

    static readonly IReadOnlyDictionary<string, string?> NoOverlay = new Dictionary<string, string?>();

    static KeyValuePair<string, string> Var(string name, string value) => new(name, value);

    /// <remarks>
    /// The terminator is checked here rather than in a test of its own, so that an entry which lost it
    /// fails every assertion in the file loudly instead of shifting one byte of every comparison.
    /// </remarks>
    static string[] Entries(byte[][] block)
    {
        var entries = new string[block.Length];

        for (var i = 0; i < block.Length; i++)
        {
            Assert.True(
                block[i].Length > 0 && block[i][^1] == 0,
                $"Entry {i} is not null-terminated, so envp would run past the end of it.");

            entries[i] = Encoding.UTF8.GetString(block[i], 0, block[i].Length - 1);
        }

        return entries;
    }
}
