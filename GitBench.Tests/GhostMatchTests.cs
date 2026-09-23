using GitBench.Features.Diff;
using GitBench.Features.Editor;
using Xunit;

namespace GitBench.Tests;

/// <summary>A draft drawn into the editor shrinks as the reader types it, line by line, and what is
/// left hangs under the last line they typed.</summary>
public sealed class GhostMatchTests
{
    private static readonly IReadOnlyList<string> Draft =
        ["public int Multiply(int a, int b)", "{", "    return a * b;", "}"];

    private static GhostLines Match(IReadOnlyList<string> draft, int below, params string[] file) =>
        GhostMatch.Remaining(draft, new FileLine(2), new FileLine(below), file.Length, n => file[n - 1]);

    [Fact]
    public void NothingTyped_TheWholeDraftHangsUnderTheAnchor()
    {
        var left = Match(Draft, 3, "class Calc", "{", "}");

        Assert.Equal(new FileLine(2), left.After);
        Assert.Equal(Draft, left.Lines);
    }

    [Fact]
    public void TypedLines_Go_AndTheRestHangsUnderTheLastOne()
    {
        var left = Match(Draft, 6, "class Calc", "{", "  public int Multiply(int a, int b)", "  {", "      return a * b;", "}");

        Assert.Equal(new FileLine(5), left.After);
        Assert.Equal(["}"], left.Lines);
    }

    [Fact]
    public void ABraceBelowTheTypedLines_IsTheFilesOwn()
    {
        var left = Match(Draft, 4, "class Calc", "{", "  public int Multiply(int a, int b)", "}");

        Assert.Equal(new FileLine(3), left.After);
        Assert.Equal(["{", "    return a * b;", "}"], left.Lines);
    }

    [Fact]
    public void LinesAlreadyInTheFile_AreNotTakenForTyped_HoweverMuchTheyReadLikeTheDraft()
    {
        var left = Match(Draft, 3, "class Calc", "{", "  public int Add(int a, int b)", "  {", "      return a * b;", "  }", "}");

        Assert.Equal(new FileLine(2), left.After);
        Assert.Equal(Draft, left.Lines);
    }

    [Fact]
    public void ShiftBelow_TakesTextPutInAtTheStartOfTheLineBelow_AsTyped()
    {
        var enterAtTheAnchorsEnd = new TextEdit(new TextRange(TextPosition.At(2, 1), TextPosition.At(3, 0)), string.Empty);
        var pastedAtTheStartOfTheLineBelow = new TextEdit(new TextRange(TextPosition.At(3, 0), TextPosition.At(5, 0)), string.Empty);
        var typedIntoTheLineBelow = new TextEdit(new TextRange(TextPosition.At(3, 2), TextPosition.At(4, 0)), string.Empty);

        Assert.Equal(new FileLine(4), GhostMatch.ShiftBelow(new FileLine(3), enterAtTheAnchorsEnd));
        Assert.Equal(new FileLine(5), GhostMatch.ShiftBelow(new FileLine(3), pastedAtTheStartOfTheLineBelow));
        Assert.Equal(new FileLine(3), GhostMatch.ShiftBelow(new FileLine(3), typedIntoTheLineBelow));
    }

    [Fact]
    public void Shift_MovesAnAnchorBelowAnEdit()
    {
        var inserted = new TextEdit(new TextRange(TextPosition.At(1, 0), TextPosition.At(3, 0)), string.Empty);

        Assert.Equal(new FileLine(7), GhostMatch.Shift(new FileLine(5), inserted));
        Assert.Equal(new FileLine(1), GhostMatch.Shift(new FileLine(1), inserted));
    }
}
