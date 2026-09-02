using GitBench.Features.Diff;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The mapping between a line's raw text and the tab-expanded text the diff draws. Every column the
/// painter, the hit-test and the syntax spans deal in is expanded; every character that leaves the
/// app is raw. This is the one place the two are allowed to meet.
/// </summary>
public sealed class DiffLineTextTests
{
    private static readonly string TabSpaces = new(' ', DiffOptions.TabWidth);

    private static ExpandedColumn Col(int value) => new(value);

    [Fact]
    public void ALineWithoutTabsIsTheSameTextInBothSpaces()
    {
        var line = DiffLineText.Of("var total = Compute();");

        Assert.Equal("var total = Compute();", line.Raw);
        Assert.Equal("var total = Compute();", line.Expanded);
        Assert.Equal("total", line.RawSlice(Col(4), Col(9)));
    }

    [Fact]
    public void ATabIsOneRawCharacterAndTabWidthExpandedColumns()
    {
        var line = DiffLineText.Of("\tx");

        Assert.Equal(TabSpaces + "x", line.Expanded);
        Assert.Equal(new ExpandedColumn(DiffOptions.TabWidth), line.ToExpanded(new RawColumn(1)));
        Assert.Equal(new RawColumn(1), line.ToRaw(Col(DiffOptions.TabWidth), TabEdge.Before));
    }

    [Theory]
    [InlineData("\tvoid Run()")]
    [InlineData("\t\treturn 1;")]
    [InlineData("  \tmixed indent")]
    [InlineData("a\tb\tc")]
    [InlineData("no tabs here")]
    [InlineData("")]
    public void EveryRawOffsetSurvivesTheRoundTrip(string raw)
    {
        var line = DiffLineText.Of(raw);

        for (var i = 0; i <= raw.Length; i++)
        {
            var expanded = line.ToExpanded(new RawColumn(i));
            // A column that came from a raw offset always sits on a character boundary, so the
            // edge is not consulted and both answers are the offset it came from.
            Assert.Equal(new RawColumn(i), line.ToRaw(expanded, TabEdge.Before));
            Assert.Equal(new RawColumn(i), line.ToRaw(expanded, TabEdge.After));
        }
    }

    // A tab is one character: there is no offset between its spaces to land on. A range that covers
    // only part of one takes it whole, so a partial drag through the indentation still pastes as
    // indentation rather than silently losing it. Columns below assume DiffOptions.TabWidth is 4.
    [Theory]
    [InlineData(0, 4, "\t")]    // exactly the tab's own columns
    [InlineData(1, 3, "\t")]    // strictly inside them
    [InlineData(2, 5, "\tx")]   // out of the tab's middle and into the next character
    [InlineData(0, 2, "\t")]    // from the tab's start into its middle
    [InlineData(4, 6, "xy")]    // clear of the tab entirely
    public void ARangeOverlappingATabTakesTheWholeTab(int from, int to, string expected)
    {
        var line = DiffLineText.Of("\txy");

        Assert.Equal(expected, line.RawSlice(Col(from), Col(to)));
    }

    [Fact]
    public void AnEmptyRangeSlicesNothingEvenInsideATab()
    {
        var line = DiffLineText.Of("\tx");

        Assert.Equal(string.Empty, line.RawSlice(Col(2), Col(2)));
        Assert.Equal(string.Empty, line.RawSlice(Col(3), Col(1)));
    }

    // Positions can outlive the row they were captured against — an async highlight re-emit lands a
    // shorter one — so out-of-range columns clamp rather than throw.
    [Fact]
    public void ColumnsPastTheEndOfTheLineClamp()
    {
        var line = DiffLineText.Of("\tab");

        Assert.Equal("\tab", line.RawSlice(Col(-5), Col(999)));
        Assert.Equal(new RawColumn(3), line.ToRaw(Col(999), TabEdge.After));
        Assert.Equal(new RawColumn(0), line.ToRaw(Col(-1), TabEdge.After));
        Assert.Equal(new ExpandedColumn(0), line.ToExpanded(new RawColumn(-1)));
    }

    [Fact]
    public void ConsecutiveTabsEachKeepTheirOwnColumns()
    {
        var line = DiffLineText.Of("\t\tx");

        Assert.Equal(TabSpaces + TabSpaces + "x", line.Expanded);
        Assert.Equal("\t\t", line.RawSlice(Col(0), Col(DiffOptions.TabWidth * 2)));
        Assert.Equal(new ExpandedColumn(DiffOptions.TabWidth * 2), line.ToExpanded(new RawColumn(2)));
    }

    // Offsets are UTF-16 code units on both sides, so a surrogate pair shifts the two spaces by
    // two, not one.
    [Fact]
    public void AnAstralCharacterCountsAsTwoColumnsInBothSpaces()
    {
        var line = DiffLineText.Of("\tvar 🎉 = 1;");

        Assert.Equal(new ExpandedColumn(DiffOptions.TabWidth + 6), line.ToExpanded(new RawColumn(7)));
        Assert.Equal("var 🎉", line.RawSlice(Col(DiffOptions.TabWidth), Col(DiffOptions.TabWidth + 6)));
    }

    [Theory]
    [InlineData(0, 0, 3)]
    [InlineData(2, 0, 3)]
    [InlineData(4, 4, 9)]
    [InlineData(8, 4, 9)]
    public void TheIdentifierUnderAColumnIsTheWholeRunAroundIt(int at, int start, int end)
    {
        var line = DiffLineText.Of("var total = 1;");

        Assert.Equal((new RawColumn(start), new RawColumn(end)), line.IdentifierAt(new RawColumn(at)));
    }

    [Fact]
    public void DigitsAndUnderscoresAndDollarsBelongToAnIdentifier()
    {
        var line = DiffLineText.Of("  _tmp2$ = x");

        Assert.Equal((new RawColumn(2), new RawColumn(8)), line.IdentifierAt(new RawColumn(4)));
    }

    // The character just past a word is the whitespace after it, and that is not the word — this is
    // what keeps a link off the gap between two identifiers.
    [Theory]
    [InlineData("a + b", 2)]
    [InlineData("   x", 1)]
    [InlineData("total = 1;", 5)]
    [InlineData("", 0)]
    public void PunctuationAndWhitespaceNameNoIdentifier(string raw, int at)
    {
        Assert.Null(DiffLineText.Of(raw).IdentifierAt(new RawColumn(at)));
    }

    // The one column here that does not clamp: off the end of the line is no identifier rather than
    // the last one, so pointing at the margin past a word is different from pointing at the word.
    [Fact]
    public void ColumnsOffTheEndsOfTheLineNameNoIdentifier()
    {
        var line = DiffLineText.Of("total");

        Assert.Null(line.IdentifierAt(new RawColumn(5)));
        Assert.Null(line.IdentifierAt(new RawColumn(999)));
        Assert.Null(line.IdentifierAt(new RawColumn(-1)));
    }

    // The identifier is measured in raw columns, so an indent's tabs neither shift nor widen it.
    [Fact]
    public void AnIdentifierAfterATabIsMeasuredInRawColumns()
    {
        var line = DiffLineText.Of("	Compute();");

        Assert.Equal((new RawColumn(1), new RawColumn(8)), line.IdentifierAt(new RawColumn(4)));
    }
}
