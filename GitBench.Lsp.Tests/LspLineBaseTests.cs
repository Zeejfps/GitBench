using Xunit;
using GitBench.Lsp;

namespace GitBench.Lsp.Tests;

/// <summary>
/// The protocol counts lines from zero; every part of the app a person looks at counts from one.
/// The two are different types so they cannot be assigned to each other, and these pin the single
/// conversion between them — including, first, that it is a conversion at all.
/// </summary>
public sealed class LspLineBaseTests
{
    // The mistake this whole split exists to prevent: a conversion that quietly does nothing. Every
    // jump would land one line off, on a real line, looking exactly like a working feature.
    [Fact]
    public void TheTwoLineSpacesAreNotTheSameNumber()
    {
        Assert.NotEqual(1, LspLine.FromOneBased(1).Value);
        Assert.NotEqual(0, new LspLine(0).ToOneBased());
    }

    [Fact]
    public void TheFirstLineOfAFileIsLineOneToAReaderAndLineZeroOnTheWire()
    {
        Assert.Equal(0, LspLine.FromOneBased(1).Value);
        Assert.Equal(1, new LspLine(0).ToOneBased());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(500)]
    [InlineData(int.MaxValue)]
    public void AOneBasedLineSurvivesTheTripToTheWireAndBack(int line) =>
        Assert.Equal(line, LspLine.FromOneBased(line).ToOneBased());

    // One-based counting has no line zero, so the boundary is refused rather than wrapped to a
    // negative the protocol cannot express.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ALineBeforeTheFirstOneIsRefused(int line) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LspLine.FromOneBased(line));
}
