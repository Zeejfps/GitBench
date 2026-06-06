using Xunit;

namespace GitBench.Tests;

// Span-to-line mapping with fabricated spans (no git, no engine): removed rows must resolve to
// the old file, added/context rows to the new file, with 1-based line numbers and safe bounds.
public class DiffHighlightTests
{
    private static IReadOnlyList<IReadOnlyList<TokenSpan>> Lines(params TokenColorSlot[] slotPerLine)
    {
        var lines = new IReadOnlyList<TokenSpan>[slotPerLine.Length];
        for (var i = 0; i < slotPerLine.Length; i++)
            lines[i] = new[] { new TokenSpan(0, 1, slotPerLine[i]) };
        return lines;
    }

    private static DiffHighlight Build()
    {
        // Old file lines carry Keyword/String/Comment; new file lines carry Number/Type/Function.
        var old = Lines(TokenColorSlot.Keyword, TokenColorSlot.String, TokenColorSlot.Comment);
        var @new = Lines(TokenColorSlot.Number, TokenColorSlot.Type, TokenColorSlot.Function);
        return new DiffHighlight(old, @new);
    }

    [Fact]
    public void RemovedLineResolvesToOldSide()
    {
        var h = Build();
        var spans = h.ForLine(DiffLineKind.Removed, oldLineNumber: 2, newLineNumber: null);
        Assert.Single(spans);
        Assert.Equal(TokenColorSlot.String, spans[0].Slot); // old line 2 → index 1
    }

    [Fact]
    public void AddedLineResolvesToNewSide()
    {
        var h = Build();
        var spans = h.ForLine(DiffLineKind.Added, oldLineNumber: null, newLineNumber: 3);
        Assert.Single(spans);
        Assert.Equal(TokenColorSlot.Function, spans[0].Slot); // new line 3 → index 2
    }

    [Fact]
    public void ContextLineResolvesToNewSide()
    {
        var h = Build();
        var spans = h.ForLine(DiffLineKind.Context, oldLineNumber: 1, newLineNumber: 1);
        Assert.Single(spans);
        Assert.Equal(TokenColorSlot.Number, spans[0].Slot); // new line 1 → index 0, not old's Keyword
    }

    [Fact]
    public void LineNumbersAreOneBased()
    {
        var h = Build();
        Assert.Equal(TokenColorSlot.Keyword, h.ForLine(DiffLineKind.Removed, 1, null)[0].Slot);
    }

    [Theory]
    [InlineData(0)]   // below 1-based range
    [InlineData(99)]  // past the end
    public void OutOfRangeLineNumberYieldsEmpty(int lineNumber)
    {
        var h = Build();
        Assert.Empty(h.ForLine(DiffLineKind.Added, null, lineNumber));
    }

    [Fact]
    public void MissingLineNumberYieldsEmpty()
    {
        var h = Build();
        Assert.Empty(h.ForLine(DiffLineKind.Removed, oldLineNumber: null, newLineNumber: null));
    }

    [Fact]
    public void NullSideYieldsEmpty()
    {
        // Pure-add diff: no old-side spans fetched. Removed lookups must degrade to empty.
        var h = new DiffHighlight(null, Lines(TokenColorSlot.Number));
        Assert.Empty(h.ForLine(DiffLineKind.Removed, oldLineNumber: 1, newLineNumber: null));
        Assert.Single(h.ForLine(DiffLineKind.Added, null, 1));
    }
}
