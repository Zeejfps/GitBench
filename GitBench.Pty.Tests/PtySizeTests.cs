using GitBench.Pty;

namespace GitBench.Pty.Tests;

public class PtySizeTests
{
    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(-1, 24)]
    [InlineData(80, -1)]
    public void RejectsEmptyDimensions(int columns, int rows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PtySize(columns, rows));
    }

    [Fact]
    public void RejectsDimensionsBeyondTheWireFormat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PtySize(ushort.MaxValue + 1, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PtySize(80, ushort.MaxValue + 1));
    }

    [Fact]
    public void DefaultIsTheClassicTerminal()
    {
        Assert.Equal(80, PtySize.Default.Columns);
        Assert.Equal(24, PtySize.Default.Rows);
    }
}
