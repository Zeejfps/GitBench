using System.Text;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// Properties of a whole recorded session that a golden cannot state and a synthesised byte string
/// cannot exercise: that nothing the program emitted leaked into the grid as text, that the cursor
/// stayed on the screen, and that each program's session-level intent survived the replay.
/// </summary>
/// <remarks>
/// The screen itself is pinned by <see cref="CorpusReplayTests"/> against a committed golden, and
/// resumability by <see cref="ChunkInvarianceTests"/>. These are the assertions that name what went
/// wrong when one of those two shows a screen full of the wrong thing — and the only assertions
/// the claude corpus has at all, since it has no golden.
/// </remarks>
public class CorpusPropertiesSpec
{
    /// <summary>
    /// Byte offsets into the claude recording, read from the raw stream and not from any engine:
    /// the ESC introducing the alt-screen exit, after which the program erases all 34 rows, and the
    /// ESC introducing a final OSC 0 whose payload is empty.
    /// </summary>
    const int AltScreenExit = 5231;
    const int TitleCleared = 5531;

    public static IEnumerable<object[]> Corpora() => Corpus.All();

    [Theory]
    [MemberData(nameof(Corpora))]
    public void Corpus_ReplaysWithoutThrowing(string name)
    {
        var corpus = Corpus.Load(name);
        using var engine = EngineUnderTest.Create(corpus.Size);

        var thrown = Record.Exception(() => engine.Feed(corpus.Bytes));

        Assert.Null(thrown);
    }

    [Theory]
    [MemberData(nameof(Corpora))]
    public void Corpus_LeavesNoEscapeSequenceTextInTheGrid(string name)
    {
        var corpus = Corpus.Load(name);
        using var engine = EngineUnderTest.Create(corpus.Size);

        engine.Feed(corpus.Bytes);

        Assert.DoesNotContain(Vt.Esc, VisibleText(engine.Grid), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Corpora))]
    public void Corpus_LeavesTheCursorInsideTheGrid(string name)
    {
        var corpus = Corpus.Load(name);
        using var engine = EngineUnderTest.Create(corpus.Size);

        engine.Feed(corpus.Bytes);

        var cursor = engine.State.Cursor;
        Assert.InRange(cursor.Column, 0, engine.Grid.Size.Columns - 1);
        Assert.InRange(cursor.Row, 0, engine.Grid.Size.Rows - 1);
    }

    /// <remarks>
    /// Not every corpus. The claude recording clears its title on the way out, so it is pinned by
    /// <see cref="Claude_SetsATitleAndThenClearsIt"/> instead.
    /// </remarks>
    [Theory]
    [InlineData("smoke")]
    [InlineData("vim")]
    [InlineData("less")]
    [InlineData("git-log")]
    public void Corpus_SetsTheWindowTitle(string name)
    {
        var corpus = Corpus.Load(name);
        using var engine = EngineUnderTest.Create(corpus.Size);

        engine.Feed(corpus.Bytes);

        Assert.NotEqual(string.Empty, engine.State.Title);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("vim")]
    [InlineData("less")]
    public void FullScreenProgram_LeavesTheAlternateScreenWhenItExits(string name)
    {
        var corpus = Corpus.Load(name);
        using var engine = EngineUnderTest.Create(corpus.Size);

        engine.Feed(corpus.Bytes);

        Assert.False(
            engine.State.Modes.AlternateScreen,
            $"{name} brackets its session in ?1049h/?1049l, so the shell's screen must be back at the end.");
    }

    [Fact]
    public void GitLog_NeverEntersTheAlternateScreen()
    {
        var corpus = Corpus.Load("git-log");
        using var engine = EngineUnderTest.Create(corpus.Size);

        engine.Feed(corpus.Bytes);

        Assert.False(engine.State.Modes.AlternateScreen);
    }

    [Fact]
    public void GitLog_ScrollsItsOutputIntoScrollback()
    {
        var corpus = Corpus.Load("git-log");
        using var engine = EngineUnderTest.Create(corpus.Size);

        engine.Feed(corpus.Bytes);

        Assert.True(
            engine.Grid.ScrollbackRows > 0,
            "Eighty log lines through a thirty-row viewport must leave lines in scrollback.");
    }

    /// <remarks>
    /// The session sets a title at byte 4749 and clears it at 5531 with an empty OSC 0 payload, so a
    /// correct terminal ends it untitled. Asserting a non-empty title at end of stream would be
    /// asserting against what the recording actually says.
    /// </remarks>
    [Fact]
    public void Claude_SetsATitleAndThenClearsIt()
    {
        var corpus = Corpus.Load("claude");
        using var engine = EngineUnderTest.Create(corpus.Size);

        engine.Feed(corpus.Bytes.AsSpan(0, TitleCleared));
        Assert.NotEqual(string.Empty, engine.State.Title);

        engine.Feed(corpus.Bytes.AsSpan(TitleCleared));
        Assert.Equal(string.Empty, engine.State.Title);
    }

    [Fact]
    public void Claude_PaintsColouredTextOntoTheGrid()
    {
        var corpus = Corpus.Load("claude");
        using var engine = EngineUnderTest.Create(corpus.Size);

        engine.Feed(corpus.Bytes.AsSpan(0, AltScreenExit));

        Assert.True(
            AnyCell(engine.Grid, cell => cell.Foreground.Kind == TerminalColorKind.Rgb),
            "The claude corpus contains 47 truecolor SGRs, so at least one visible cell must carry an RGB foreground.");
    }

    [Fact]
    public void Claude_EndsWithSgrMouseEncodingTurnedBackOff()
    {
        var corpus = Corpus.Load("claude");
        using var engine = EngineUnderTest.Create(corpus.Size);

        engine.Feed(corpus.Bytes);

        Assert.Equal(MouseEncoding.X10, engine.State.Modes.MouseEncoding);
    }

    static string VisibleText(ITerminalGrid grid)
    {
        var text = new StringBuilder();
        for (var row = -grid.ScrollbackRows; row < grid.Size.Rows; row++)
            text.Append(grid.RowText(row)).Append('\n');

        return text.ToString();
    }

    static bool AnyCell(ITerminalGrid grid, Func<TerminalCell, bool> predicate)
    {
        var cells = new TerminalCell[grid.Size.Columns];
        for (var row = -grid.ScrollbackRows; row < grid.Size.Rows; row++)
        {
            grid.CopyRow(row, cells);
            if (cells.Any(predicate))
                return true;
        }

        return false;
    }
}
