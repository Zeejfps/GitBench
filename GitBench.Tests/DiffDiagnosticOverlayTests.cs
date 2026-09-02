using GitBench.Features.Diff;
using GitBench.Lsp;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Where the protocol's coordinates meet the ones the painter draws in. Two crossings happen here
/// and each is invisible when wrong: the protocol counts lines from zero and the gutter from one,
/// and a diagnostic's characters are the file's own while a row is drawn with its tabs expanded.
/// </summary>
public sealed class DiffDiagnosticOverlayTests
{
    private static Diagnostic At(
        int startLine, int startChar, int endLine, int endChar,
        DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        new(
            new LspRange(
                new LspPosition(new LspLine(startLine), new LspCharacter(startChar)),
                new LspPosition(new LspLine(endLine), new LspCharacter(endChar))),
            severity,
            "something is wrong");

    private static DiffDiagnosticOverlay Overlay(params Diagnostic[] items) => new("/repo/main.rs", items);

    // The whole zero-versus-one crossing, on its own: the server's first line is the gutter's line
    // one, and an overlay that skipped the conversion would mark line zero, which is no line.
    [Fact]
    public void TheServersFirstLineIsTheGuttersLineOne()
    {
        var overlay = Overlay(At(0, 0, 0, 3));

        Assert.NotEmpty(overlay.MarksOn(new FileLine(1), DiffLineText.Of("let x = 1;")));
        Assert.Empty(overlay.MarksOn(new FileLine(2), DiffLineText.Of("let x = 1;")));
    }

    [Fact]
    public void AMarkCoversTheCharactersTheServerNamed()
    {
        var overlay = Overlay(At(0, 4, 0, 9));

        var mark = Assert.Single(overlay.MarksOn(new FileLine(1), DiffLineText.Of("let value = 1;")));

        Assert.Equal(new CharRange(4, 5), mark.Range);
    }

    // A tab is one character to the server and four columns to the painter. A mark that skipped the
    // expansion would sit three columns left of the code it is about, on a real column, looking
    // deliberate.
    [Fact]
    public void AMarkOnATabIndentedLineIsMeasuredInDrawnColumns()
    {
        var overlay = Overlay(At(0, 1, 0, 6));

        var mark = Assert.Single(overlay.MarksOn(new FileLine(1), DiffLineText.Of("\tvalue")));

        Assert.Equal(new CharRange(4, 5), mark.Range);
    }

    [Fact]
    public void AMultiLineRangeMarksEachLineItCrosses()
    {
        var overlay = Overlay(At(0, 6, 2, 3));

        var first = Assert.Single(overlay.MarksOn(new FileLine(1), DiffLineText.Of("struct Point {")));
        var middle = Assert.Single(overlay.MarksOn(new FileLine(2), DiffLineText.Of("    x: i32,")));
        var last = Assert.Single(overlay.MarksOn(new FileLine(3), DiffLineText.Of("    y: i32,")));

        Assert.Equal(new CharRange(6, 8), first.Range);
        Assert.Equal(new CharRange(0, 11), middle.Range);
        Assert.Equal(new CharRange(0, 3), last.Range);
    }

    // A range ending at character zero of a line stops at the newline before it. Marking that line
    // would underline a row the diagnostic never reached.
    [Fact]
    public void ARangeEndingAtTheStartOfALineDoesNotMarkThatLine()
    {
        var overlay = Overlay(At(0, 2, 1, 0));

        Assert.NotEmpty(overlay.MarksOn(new FileLine(1), DiffLineText.Of("let x = 1;")));
        Assert.Empty(overlay.MarksOn(new FileLine(2), DiffLineText.Of("let y = 2;")));
    }

    // "Expected a semicolon here" points between two characters. Drawn at its true width it is
    // invisible, which reads as no diagnostic at all.
    [Fact]
    public void ARangeWithNoWidthIsStillWideEnoughToSee()
    {
        var overlay = Overlay(At(0, 9, 0, 9));

        var mark = Assert.Single(overlay.MarksOn(new FileLine(1), DiffLineText.Of("let x = 1")));

        Assert.Equal(new CharRange(9, 1), mark.Range);
    }

    [Fact]
    public void ARangeSentBackwardsIsReadTheWayItWasMeant()
    {
        var overlay = Overlay(At(0, 9, 0, 4));

        var mark = Assert.Single(overlay.MarksOn(new FileLine(1), DiffLineText.Of("let value = 1;")));

        Assert.Equal(new CharRange(4, 5), mark.Range);
    }

    [Fact]
    public void ALineWithNothingWrongWithItHasNoMarks()
    {
        var overlay = Overlay(At(4, 0, 4, 2));

        Assert.Empty(overlay.MarksOn(new FileLine(1), DiffLineText.Of("let x = 1;")));
        Assert.Null(overlay.SeverityOf(new FileLine(1)));
    }

    [Fact]
    public void TwoDiagnosticsOnOneLineBothMarkIt()
    {
        var overlay = Overlay(At(0, 0, 0, 3), At(0, 8, 0, 9));

        Assert.Equal(2, overlay.MarksOn(new FileLine(1), DiffLineText.Of("let x = 1;")).Count);
    }

    // The gutter has room for one mark per line, so a line carrying both an error and a warning is
    // marked as an error: the worse of the two is the one worth crossing the room to see.
    [Fact]
    public void ALineCarryingBothIsMarkedWithTheWorseOfThem()
    {
        var overlay = Overlay(
            At(0, 0, 0, 3, DiagnosticSeverity.Warning),
            At(0, 8, 0, 9, DiagnosticSeverity.Error));

        Assert.Equal(DiagnosticSeverity.Error, overlay.SeverityOf(new FileLine(1)));
    }

    [Fact]
    public void ASeverityTheServerLeftOutIsTreatedAsAnError()
    {
        var overlay = Overlay(At(0, 0, 0, 3, DiagnosticSeverity.Unspecified));

        Assert.Equal(DiagnosticSeverity.Error, overlay.SeverityOf(new FileLine(1)));
    }

    [Fact]
    public void TheGutterMarksEveryLineAMultiLineRangeCrosses()
    {
        var overlay = Overlay(At(1, 0, 3, 2));

        Assert.Null(overlay.SeverityOf(new FileLine(1)));
        Assert.Equal(DiagnosticSeverity.Error, overlay.SeverityOf(new FileLine(2)));
        Assert.Equal(DiagnosticSeverity.Error, overlay.SeverityOf(new FileLine(3)));
        Assert.Equal(DiagnosticSeverity.Error, overlay.SeverityOf(new FileLine(4)));
    }

    [Fact]
    public void AnOverlayWithNothingInItSaysSo()
    {
        Assert.True(DiffDiagnosticOverlay.Empty.IsEmpty);
        Assert.Empty(DiffDiagnosticOverlay.Empty.MarksOn(new FileLine(1), DiffLineText.Of("x")));
    }

    [Fact]
    public void TheMessagesForALineAreKeptForTheCardThatShowsThem()
    {
        var overlay = Overlay(At(0, 0, 0, 3), At(2, 0, 2, 1));

        Assert.Single(overlay.On(new FileLine(1)));
        Assert.Empty(overlay.On(new FileLine(2)));
        Assert.Single(overlay.On(new FileLine(3)));
    }
}
