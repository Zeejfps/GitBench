using System.Text;
using GitBench.Features.Terminal;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using GitBench.Terminal.Vt.Adapters;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The span arithmetic, which is pure and is where a selection can go wrong without anything on
/// screen saying so.
/// </summary>
public class TerminalSpanTests
{
    static readonly GridBounds Screen = new(Columns: 80, Rows: 24, ScrollbackRows: 100);

    [Fact]
    public void ASpanDraggedBackwards_IsOrderedRatherThanInverted()
    {
        var span = TerminalSpan.Between(new GridPoint(5, 3), new GridPoint(2, 1), Screen);

        Assert.Equal(new GridPoint(2, 1), span?.Start);
        Assert.Equal(new GridPoint(5, 3), span?.End);
    }

    [Fact]
    public void APointOffTheGrid_IsPulledOntoIt()
    {
        var span = TerminalSpan.Between(new GridPoint(-4, -500), new GridPoint(900, 900), Screen);

        Assert.Equal(new GridPoint(0, -100), span?.Start);
        Assert.Equal(new GridPoint(79, 23), span?.End);
    }

    // ---- carried by output ----

    [Fact]
    public void OutputScrollingTheScreen_MovesTheSelectionWithTheTextItCovers()
    {
        var span = TerminalSpan.Between(new GridPoint(0, 5), new GridPoint(9, 5), Screen);

        var shifted = TerminalSpan.Shift(span!.Value, linesScrolled: 3, Screen);

        Assert.Equal(new GridPoint(0, 2), shifted?.Start);
        Assert.Equal(new GridPoint(9, 2), shifted?.End);
    }

    /// <remarks>
    /// Dropped, not clamped. Clamping would move the ends onto rows the user never highlighted, and
    /// the copy that followed would hand them text they never selected.
    /// </remarks>
    [Fact]
    public void ASelectionScrolledOutOfTheHistoryEntirely_IsDroppedRatherThanClamped()
    {
        var span = TerminalSpan.Between(new GridPoint(0, -99), new GridPoint(9, -99), Screen);

        Assert.Null(TerminalSpan.Shift(span!.Value, linesScrolled: 50, Screen));
    }

    [Fact]
    public void ASelectionHalfOutOfTheHistory_KeepsOnlyTheHalfThatSurvives()
    {
        var span = TerminalSpan.Between(new GridPoint(3, -99), new GridPoint(9, 0), Screen);

        var shifted = TerminalSpan.Shift(span!.Value, linesScrolled: 5, Screen);

        Assert.Equal(-100, shifted?.Start.Row);
        Assert.Equal(new GridPoint(9, -5), shifted?.End);
    }

    [Fact]
    public void AHistoryThatShrinksUnderTheSelection_DropsWhatIsNoLongerThere()
    {
        var span = TerminalSpan.Between(new GridPoint(0, -90), new GridPoint(9, -90), Screen);

        // CSI 3J and RIS both empty the history with no line ever leaving the screen, so there is no
        // scroll count to carry the selection by — only the bounds it no longer fits.
        Assert.Null(TerminalSpan.Surviving(span!.Value, Screen with { ScrollbackRows = 0 }));
    }

    // ---- what a row asks the painter ----

    [Fact]
    public void AMiddleRowOfAMultiRowSelection_IsSelectedEndToEnd()
    {
        var span = TerminalSpan.Between(new GridPoint(4, 0), new GridPoint(6, 2), Screen);

        Assert.True(span!.Value.TryColumnsOn(1, columns: 80, out var first, out var last));
        Assert.Equal(0, first);
        Assert.Equal(79, last);
    }

    [Fact]
    public void TheFirstAndLastRows_AreSelectedOnlyFromAndToThePointsDragged()
    {
        var span = TerminalSpan.Between(new GridPoint(4, 0), new GridPoint(6, 2), Screen);

        Assert.True(span!.Value.TryColumnsOn(0, columns: 80, out var firstStart, out _));
        Assert.True(span.Value.TryColumnsOn(2, columns: 80, out _, out var lastEnd));
        Assert.Equal(4, firstStart);
        Assert.Equal(6, lastEnd);
    }

    [Fact]
    public void ARowOutsideTheSelection_IsNotPainted()
    {
        var span = TerminalSpan.Between(new GridPoint(0, 1), new GridPoint(9, 1), Screen);

        Assert.False(span!.Value.TryColumnsOn(2, columns: 80, out _, out _));
    }
}

/// <summary>
/// Turning a span into the text that reaches the clipboard, read off a real engine.
/// </summary>
public class TerminalSelectionTextTests
{
    [Fact]
    public void OneRow_CopiesTheCharactersItCovers()
    {
        using var screen = Screen.Of("hello world");

        Assert.Equal("hello", screen.Copy(new GridPoint(0, 0), new GridPoint(4, 0)));
    }

    [Fact]
    public void TrailingBlanks_AreTrimmedRatherThanCopiedAsSpaces()
    {
        using var screen = Screen.Of("hi");

        Assert.Equal("hi", screen.Copy(new GridPoint(0, 0), new GridPoint(40, 0)));
    }

    [Fact]
    public void TwoRealRows_AreJoinedWithANewline()
    {
        using var screen = Screen.Of("one\r\ntwo");

        Assert.Equal("one\ntwo", screen.Copy(new GridPoint(0, 0), new GridPoint(2, 1)));
    }

    /// <remarks>
    /// The reason <c>ITerminalGrid.ContinuesPreviousRow</c> is on the seam at all. A line that ran
    /// past the right margin is one line, and copying it with a newline in the middle produces text
    /// that does not paste back as what was on screen.
    /// </remarks>
    [Fact]
    public void ARowThatWrapped_IsCopiedAsOneLineWithNoNewlineInIt()
    {
        using var screen = Screen.Of(new string('a', 12), columns: 10);

        var copied = screen.Copy(new GridPoint(0, 0), new GridPoint(1, 1));

        Assert.Equal(new string('a', 12), copied);
        Assert.DoesNotContain('\n', copied);
    }

    [Fact]
    public void AWideCharacter_IsCopiedOnceRatherThanOncePerColumn()
    {
        using var screen = Screen.Of("字");

        Assert.Equal("字", screen.Copy(new GridPoint(0, 0), new GridPoint(1, 0)));
    }

    [Fact]
    public void ASpanTheGridNoLongerHas_CopiesNothingRatherThanThrowing()
    {
        using var screen = Screen.Of("hi");

        // Rows below the history are off the grid, and CopyRow throws on them. Reaching one has to
        // be a wrong answer at worst, never an exception on the thread that owns the window.
        Assert.Equal(string.Empty, screen.CopyRaw(new GridPoint(0, -900), new GridPoint(1, -900)));
    }

    // ---- granularity ----

    [Fact]
    public void ADoubleClick_TakesTheWholeWordUnderIt()
    {
        using var screen = Screen.Of("alpha beta gamma");

        Assert.Equal("beta", screen.Copy(new GridPoint(7, 0), new GridPoint(7, 0), SelectionGranularity.Word));
    }

    [Fact]
    public void AWord_StopsAtWhitespaceRatherThanRunningToTheMargin()
    {
        using var screen = Screen.Of("alpha beta");

        Assert.Equal("alpha", screen.Copy(new GridPoint(2, 0), new GridPoint(2, 0), SelectionGranularity.Word));
    }

    [Fact]
    public void APath_IsOneWordBecauseThatIsWhatADoubleClickIsFor()
    {
        using var screen = Screen.Of("see src/main.cs now");

        Assert.Equal(
            "src/main.cs",
            screen.Copy(new GridPoint(6, 0), new GridPoint(6, 0), SelectionGranularity.Word));
    }

    [Fact]
    public void ATripleClick_TakesTheWholeLine()
    {
        using var screen = Screen.Of("alpha beta gamma");

        Assert.Equal(
            "alpha beta gamma",
            screen.Copy(new GridPoint(6, 0), new GridPoint(6, 0), SelectionGranularity.Line));
    }

    /// <remarks>
    /// A wrapped line is one line to a triple click too, which is the same rule the newline join
    /// above follows and the reason both read the same grid member.
    /// </remarks>
    [Fact]
    public void ATripleClickOnAWrappedRow_TakesTheWholeLogicalLine()
    {
        using var screen = Screen.Of(new string('a', 12), columns: 10);

        Assert.Equal(
            new string('a', 12),
            screen.Copy(new GridPoint(0, 1), new GridPoint(0, 1), SelectionGranularity.Line));
    }
}

/// <summary>A real engine with a screen on it, and no shell behind it.</summary>
internal sealed class Screen : IDisposable
{
    readonly ITerminalEngine _engine;

    Screen(ITerminalEngine engine) => _engine = engine;

    public static Screen Of(string output, int columns = 80, int rows = 24)
    {
        var engine = new XtermSharpEngine(new TerminalSetup(new TerminalSize(columns, rows), 100));
        engine.Feed(Encoding.UTF8.GetBytes(output));
        return new Screen(engine);
    }

    public string Copy(
        GridPoint anchor,
        GridPoint focus,
        SelectionGranularity granularity = SelectionGranularity.Character)
    {
        var span = TerminalSelectionText.Resolve(_engine.Grid, anchor, focus, granularity);
        return span is null ? string.Empty : TerminalSelectionText.Build(_engine.Grid, span.Value);
    }

    /// <summary>Builds from a span that was never resolved against this grid, as a stale one is.</summary>
    public string CopyRaw(GridPoint start, GridPoint end)
    {
        var span = TerminalSpan.Between(
            start,
            end,
            new GridBounds(_engine.Grid.Size.Columns, _engine.Grid.Size.Rows, 1000));

        return span is null ? string.Empty : TerminalSelectionText.Build(_engine.Grid, span.Value);
    }

    public void Dispose() => _engine.Dispose();
}
