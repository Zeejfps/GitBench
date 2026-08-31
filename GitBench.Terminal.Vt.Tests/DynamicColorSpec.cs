using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// OSC 10, 11 and 12 — how a program finds out what the pane looks like.
/// </summary>
/// <remarks>
/// The load-bearing property is that silence and a wrong answer are not interchangeable. A program
/// asks because it is about to choose colours, and one that gets no reply keeps whatever assumption
/// it started with; one that gets an invented reply commits to it. So an engine with no renderer
/// behind it says nothing, and an engine with one says what is actually on screen.
/// </remarks>
public class DynamicColorSpec
{
    /// <summary>A palette whose three slots are distinguishable at a glance in a failure message.</summary>
    sealed class StubPalette : ITerminalPalette
    {
        public TerminalRgb Foreground { get; set; } = new(0x11, 0x22, 0x33);
        public TerminalRgb Background { get; set; } = new(0xFF, 0xFF, 0xFF);
        public TerminalRgb Cursor { get; set; } = new(0x4F, 0x46, 0xE5);

        public int Asked { get; private set; }

        public TerminalRgb Resolve(TerminalColorSlot slot)
        {
            Asked++;

            return slot switch
            {
                TerminalColorSlot.Foreground => Foreground,
                TerminalColorSlot.Background => Background,
                TerminalColorSlot.Cursor => Cursor,
                _ => throw new ArgumentOutOfRangeException(nameof(slot)),
            };
        }
    }

    static ITerminalEngine Engine(ITerminalPalette? palette) =>
        EngineUnderTest.Create(new TerminalSetup(new TerminalSize(20, 6), 100) { Palette = palette });

    [Fact]
    public void AskingForTheBackground_AnswersWithWhatTheRendererDrawsIt()
    {
        var palette = new StubPalette { Background = new(0xFF, 0xFF, 0xFF) };
        using var engine = Engine(palette);

        var result = engine.Feed($"{Osc}11;?{St}");

        Assert.Equal($"{Osc}11;rgb:ffff/ffff/ffff{St}", result.Text());
    }

    [Fact]
    public void AskingForTheForeground_AnswersOnItsOwnIdentifier()
    {
        var palette = new StubPalette { Foreground = new(0x1F, 0x29, 0x37) };
        using var engine = Engine(palette);

        var result = engine.Feed($"{Osc}10;?{St}");

        Assert.Equal($"{Osc}10;rgb:1f1f/2929/3737{St}", result.Text());
    }

    [Fact]
    public void AskingForTheCursor_AnswersOnItsOwn()
    {
        var palette = new StubPalette { Cursor = new(0x4F, 0x46, 0xE5) };
        using var engine = Engine(palette);

        var result = engine.Feed($"{Osc}12;?{St}");

        Assert.Equal($"{Osc}12;rgb:4f4f/4646/e5e5{St}", result.Text());
    }

    /// <remarks>
    /// Each channel is doubled rather than shifted up, so a channel that is full reports as
    /// <c>ffff</c>. A client that divides the reply back down to eight bits has to land on the value
    /// that was sent, and <c>ff00</c> would come back as <c>fe</c>.
    /// </remarks>
    [Fact]
    public void TheReply_ScalesEachChannelToSixteenBitsByDoublingIt()
    {
        using var engine = Engine(new StubPalette { Background = new(0x00, 0x80, 0xFF) });

        var result = engine.Feed($"{Osc}11;?{St}");

        Assert.Equal($"{Osc}11;rgb:0000/8080/ffff{St}", result.Text());
    }

    /// <remarks>
    /// The <c>Pt</c> field repeats, each entry addressing the next slot up from the one the sequence
    /// named. Half-answering a chain reads to the caller as no support at all.
    /// </remarks>
    [Fact]
    public void AChainedQuery_AnswersEverySlotItWalksOnto()
    {
        var palette = new StubPalette
        {
            Foreground = new(0x1F, 0x29, 0x37),
            Background = new(0xFF, 0xFF, 0xFF),
        };
        using var engine = Engine(palette);

        var result = engine.Feed($"{Osc}10;?;?{St}");

        Assert.Equal(
            $"{Osc}10;rgb:1f1f/2929/3737{St}{Osc}11;rgb:ffff/ffff/ffff{St}",
            result.Text());
    }

    [Fact]
    public void AChainWalkingPastTheLastSlotItKnows_StopsRatherThanWrapping()
    {
        using var engine = Engine(new StubPalette());

        var result = engine.Feed($"{Osc}12;?;?{St}");

        Assert.Equal($"{Osc}12;rgb:4f4f/4646/e5e5{St}", result.Text());
    }

    // ---- what the engine will not do ----

    /// <remarks>
    /// The theme owns the surface. A program that could repaint it would leave the pane a colour the
    /// user's own light/dark switch no longer reaches, and a program that died mid-session would
    /// leave it there for good.
    /// </remarks>
    [Fact]
    public void SettingAColour_IsIgnoredAndLeavesTheAnswerAlone()
    {
        var palette = new StubPalette { Background = new(0xFF, 0xFF, 0xFF) };
        using var engine = Engine(palette);

        var set = engine.Feed($"{Osc}11;#ff0000{St}");
        var asked = engine.Feed($"{Osc}11;?{St}");

        Assert.False(set.HasResponse, $"A set was answered: {set.Printable()}");
        Assert.Equal($"{Osc}11;rgb:ffff/ffff/ffff{St}", asked.Text());
    }

    [Fact]
    public void AChainThatTurnsIntoASet_AnswersTheQuestionsBeforeItAndStops()
    {
        using var engine = Engine(new StubPalette { Foreground = new(0x1F, 0x29, 0x37) });

        var result = engine.Feed($"{Osc}10;?;#ff0000{St}");

        Assert.Equal($"{Osc}10;rgb:1f1f/2929/3737{St}", result.Text());
    }

    [Fact]
    public void WithNoPaletteBehindIt_TheEngineSaysNothingRatherThanInventingAColour()
    {
        using var engine = Engine(palette: null);

        var result = engine.Feed($"{Osc}11;?{St}");

        Assert.False(result.HasResponse, $"An engine with no renderer answered: {result.Printable()}");
    }

    [Fact]
    public void AQueryDoesNotLeakItsPayloadIntoTheGrid()
    {
        using var engine = Engine(new StubPalette());

        engine.Feed($"{Osc}11;?{St}after");

        Assert.Equal("after", engine.RowText(0));
    }

    // ---- the properties every sequence in this engine has to hold ----

    /// <remarks>
    /// A pseudo-terminal hands over whatever was in the pipe, so the sequence arrives in pieces. The
    /// reply is owed once, on the feed that completes it.
    /// </remarks>
    [Fact]
    public void AQuerySplitAcrossFeeds_IsAnsweredOnceWhenItCompletes()
    {
        var palette = new StubPalette { Background = new(0xFF, 0xFF, 0xFF) };
        using var engine = Engine(palette);

        var replies = new List<string>();
        foreach (var piece in new[] { $"{Osc}1", "1;", "?", St })
        {
            var result = engine.Feed(piece);
            if (result.HasResponse) replies.Add(result.Text());
        }

        Assert.Equal([$"{Osc}11;rgb:ffff/ffff/ffff{St}"], replies);
        Assert.Equal(1, palette.Asked);
    }

    /// <remarks>
    /// A program that sends two questions in one write reads the answers back in one stream and
    /// cannot tell them apart by anything but their order. This is why the answer is composed during
    /// the parse rather than collected and appended after it.
    /// </remarks>
    [Fact]
    public void RepliesComeBackInTheOrderTheQuestionsWereAsked()
    {
        using var engine = Engine(new StubPalette { Background = new(0xFF, 0xFF, 0xFF) });

        var result = engine.Feed($"{Csi}6n{Osc}11;?{St}");

        Assert.Equal($"{Csi}1;1R{Osc}11;rgb:ffff/ffff/ffff{St}", result.Text());
    }

    [Fact]
    public void AQueryBeforeACursorReport_StillComesBackFirst()
    {
        using var engine = Engine(new StubPalette { Background = new(0xFF, 0xFF, 0xFF) });

        var result = engine.Feed($"{Osc}11;?{St}{Csi}6n");

        Assert.Equal($"{Osc}11;rgb:ffff/ffff/ffff{St}{Csi}1;1R", result.Text());
    }
}
