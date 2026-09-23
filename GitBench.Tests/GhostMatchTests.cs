using GitBench.Features.Diff;
using GitBench.Features.Editor;
using Xunit;

namespace GitBench.Tests;

/// <summary>A draft drawn into the editor shrinks as the reader types it, line by line, and what is
/// left hangs under the last line they typed.</summary>
public sealed class GhostMatchTests
{
    private static readonly EditorGhost Draft = new(
        new FileLine(2),
        ["public int Multiply(int a, int b)", "{", "    return a * b;", "}"],
        ShrinksAsTyped: true);

    private static GhostLines Match(EditorGhost ghost, params string[] file) =>
        GhostMatch.Remaining(ghost, ghost.After, file.Length, n => file[n - 1]);

    [Fact]
    public void NothingTyped_TheWholeDraftHangsUnderTheAnchor()
    {
        var left = Match(Draft, "class Calc", "{", "}");

        Assert.Equal(new FileLine(2), left.After);
        Assert.Equal(Draft.Lines, left.Lines);
    }

    [Fact]
    public void TypedLines_Go_AndTheRestHangsUnderTheLastOne()
    {
        var left = Match(Draft, "class Calc", "{", "  public int Multiply(int a, int b)", "  {", "      return a * b;", "}");

        Assert.Equal(new FileLine(5), left.After);
        Assert.Equal(["}"], left.Lines);
    }

    [Fact]
    public void ABraceBelowTheTypedLines_IsTheFilesOwn()
    {
        var left = Match(Draft, "class Calc", "{", "  public int Multiply(int a, int b)", "}");

        Assert.Equal(new FileLine(3), left.After);
        Assert.Equal(["{", "    return a * b;", "}"], left.Lines);
    }

    [Fact]
    public void AShape_NeverShrinks()
    {
        var shape = Draft with { ShrinksAsTyped = false };

        Assert.Equal(shape.Lines, Match(shape, "class Calc", "{", "public int Multiply(int a, int b)").Lines);
    }

    [Fact]
    public void Shift_MovesAnAnchorBelowAnEdit()
    {
        var inserted = new TextEdit(new TextRange(TextPosition.At(1, 0), TextPosition.At(3, 0)), string.Empty);

        Assert.Equal(new FileLine(7), GhostMatch.Shift(new FileLine(5), inserted));
        Assert.Equal(new FileLine(1), GhostMatch.Shift(new FileLine(1), inserted));
    }
}
