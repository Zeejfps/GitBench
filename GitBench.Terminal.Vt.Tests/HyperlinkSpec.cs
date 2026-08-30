using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// OSC 8. A program marks a stretch of cells as a link and the grid has to remember which stretch
/// and to where, across wraps, reflows and everything else that moves a cell after it was printed.
/// </summary>
/// <remarks>
/// <para>
/// Two properties carry most of these cases. A link's extent is "the cells sharing its id", so the
/// cases that matter are the ones where the id must and must not be shared. And an id is never
/// reused, so an id that stops resolving is a cell that reads as text — never a cell that points
/// somewhere the program did not name.
/// </para>
/// <para>
/// What is <em>not</em> asserted here is any opinion about the url. The engine hands back what the
/// program wrote; whether it is worth opening belongs to the application, and
/// <c>TerminalLinkTargetTests</c> is where that lives.
/// </para>
/// </remarks>
public class HyperlinkSpec
{
    const string Docs = "https://example.com/docs";

    static string Open(string uri, string parameters = "") => $"{Osc}8;{parameters};{uri}{St}";

    static readonly string Close = $"{Osc}8;;{St}";

    static string LinkOf(ITerminalEngine engine, int column, int row)
    {
        var cell = engine.CellAt(column, row);
        return engine.Grid.TryGetHyperlink(cell.Hyperlink, out var link) ? link.Uri : string.Empty;
    }

    [Fact]
    public void PlainText_BelongsToNoLink()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed("docs");

        Assert.True(engine.CellAt(0, 0).Hyperlink.IsNone);
        Assert.False(engine.Grid.TryGetHyperlink(engine.CellAt(0, 0).Hyperlink, out _));
    }

    [Fact]
    public void TheCellsBetweenTheSequences_CarryTheLink()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"see {Open(Docs)}docs{Close} now");

        Assert.Equal(string.Empty, LinkOf(engine, 3, 0));
        Assert.Equal(Docs, LinkOf(engine, 4, 0));
        Assert.Equal(Docs, LinkOf(engine, 7, 0));
        Assert.Equal(string.Empty, LinkOf(engine, 8, 0));
    }

    [Fact]
    public void EveryCellOfOneLink_SharesOneId()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}docs{Close}");

        var id = engine.CellAt(0, 0).Hyperlink;
        Assert.False(id.IsNone);
        for (var column = 1; column < 4; column++)
            Assert.Equal(id, engine.CellAt(column, 0).Hyperlink);
    }

    /// <remarks>
    /// The reason the id lives beside the attribute rather than inside it. Only another OSC 8 ends a
    /// link, and <c>CurAttr = DefaultAttr</c> on <c>SGR 0</c> would end every one of them at the
    /// next <c>ESC[m</c> — which is what a program emits between the link text and everything after
    /// it. xterm.js keeps the url id out of its own SGR 0 reset for exactly this.
    /// </remarks>
    [Fact]
    public void SgrReset_DoesNotEndTheLink()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}{Csi}4ma{Csi}0mb{Close}");

        Assert.Equal(Docs, LinkOf(engine, 0, 0));
        Assert.Equal(Docs, LinkOf(engine, 1, 0));
    }

    /// <remarks>
    /// The trailer is filled from the same blank the insert-mode shift uses, and that blank carries
    /// only the attribute unless the link is put on it too. A link with a hole where a wide glyph
    /// sits is a highlight with a gap in it.
    /// </remarks>
    [Fact]
    public void BothColumnsOfAWideCharacter_CarryTheLink()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}世{Close}");

        Assert.Equal(CellWidth.WideLeader, engine.CellAt(0, 0).Width);
        Assert.Equal(CellWidth.WideTrailer, engine.CellAt(1, 0).Width);
        Assert.Equal(engine.CellAt(0, 0).Hyperlink, engine.CellAt(1, 0).Hyperlink);
        Assert.Equal(Docs, LinkOf(engine, 1, 0));
    }

    [Fact]
    public void ErasingACell_TakesItsLinkWithIt()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}docs{Close}{Csi}2K");

        Assert.True(engine.CellAt(0, 0).Hyperlink.IsNone);
    }

    [Fact]
    public void ALinkLeftOpen_KeepsClaimingWhatIsPrintedAfterIt()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}docs and more");

        Assert.Equal(Docs, LinkOf(engine, 12, 0));
    }

    // ---- closing ----

    [Fact]
    public void AnEmptyUrl_ClosesTheLink()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}a{Close}b");

        Assert.Equal(Docs, LinkOf(engine, 0, 0));
        Assert.Equal(string.Empty, LinkOf(engine, 1, 0));
    }

    [Fact]
    public void AnEmptyUrlWithParameters_ClosesTheLink()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}a{Osc}8;id=x;{St}b");

        Assert.Equal(string.Empty, LinkOf(engine, 1, 0));
    }

    /// <remarks>
    /// Malformed, and the open link is left alone rather than closed. Ending a link early strands
    /// the rest of its text as plain, which is the more visible of the two wrong answers; xterm.js
    /// reports the same payload unhandled, which has the same effect.
    /// </remarks>
    [Fact]
    public void AnOscEightWithNoSecondSemicolon_LeavesTheOpenLinkAlone()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}a{Osc}8;malformed{St}b");

        Assert.Equal(Docs, LinkOf(engine, 0, 0));
        Assert.Equal(Docs, LinkOf(engine, 1, 0));
    }

    /// <remarks>
    /// With no <c>;</c> at all the payload never reaches the handler — the parser's own identifier
    /// split sends it to the fallback. Pinned because that is the parser's behaviour and not this
    /// handler's, and a change to the former would silently change this.
    /// </remarks>
    [Fact]
    public void AnOscEightWithNoSemicolonAtAll_LeavesTheOpenLinkAlone()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}a{Osc}8{St}b");

        Assert.Equal(Docs, LinkOf(engine, 1, 0));
    }

    /// <remarks>Legal, and explicitly so: a program may switch links without closing the first.</remarks>
    [Fact]
    public void OpeningASecondLinkWithoutClosingTheFirst_EndsTheFirst()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open("https://a.example")}a{Open("https://b.example")}b");

        Assert.Equal("https://a.example", LinkOf(engine, 0, 0));
        Assert.Equal("https://b.example", LinkOf(engine, 1, 0));
        Assert.NotEqual(engine.CellAt(0, 0).Hyperlink, engine.CellAt(1, 0).Hyperlink);
    }

    // ---- the id parameter ----

    [Fact]
    public void TwoRunsSharingAnId_AreOneLink()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs, "id=x")}a{Close} {Open(Docs, "id=x")}b{Close}");

        Assert.Equal(engine.CellAt(0, 0).Hyperlink, engine.CellAt(2, 0).Hyperlink);
    }

    [Fact]
    public void TwoRunsWithNoId_AreTwoLinksEvenToTheSameUrl()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}a{Close} {Open(Docs)}b{Close}");

        Assert.NotEqual(engine.CellAt(0, 0).Hyperlink, engine.CellAt(2, 0).Hyperlink);
        Assert.Equal(Docs, LinkOf(engine, 2, 0));
    }

    [Fact]
    public void TheSameIdOnADifferentUrl_IsADifferentLink()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open("https://a.example", "id=x")}a{Close}{Open("https://b.example", "id=x")}b{Close}");

        Assert.NotEqual(engine.CellAt(0, 0).Hyperlink, engine.CellAt(1, 0).Hyperlink);
    }

    /// <remarks>
    /// The specification says a cell with an empty id and a cell with no id are interchangeable.
    /// Interning on the empty string instead would make every id-less link to one url a single link
    /// across the whole session, and hovering one would light up all of them.
    /// </remarks>
    [Fact]
    public void AnEmptyIdParameter_MeansNoIdAtAll()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs, "id=")}a{Close} {Open(Docs, "id=")}b{Close}");

        Assert.NotEqual(engine.CellAt(0, 0).Hyperlink, engine.CellAt(2, 0).Hyperlink);
    }

    /// <remarks>
    /// The parameter field is colon-separated and <c>id</c> need not be first. The corpus only ever
    /// sends a lone <c>id=</c>, so this is the case a sample-driven parser gets wrong.
    /// </remarks>
    [Theory]
    [InlineData("id=x", "id=x", true)]
    [InlineData("foo=bar:id=x", "id=x", true)]
    [InlineData("id=x:foo=bar", "foo=bar:id=x", true)]
    [InlineData("foo=bar", "foo=bar", false)]
    [InlineData("", "", false)]
    public void TheIdIsFoundAmongColonSeparatedParameters(string first, string second, bool sameLink)
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs, first)}a{Close} {Open(Docs, second)}b{Close}");

        Assert.Equal(sameLink, engine.CellAt(0, 0).Hyperlink == engine.CellAt(2, 0).Hyperlink);
    }

    /// <remarks>An opaque token, never percent-decoded: the corpus sends <c>id=u-c415zw%26668400</c>.</remarks>
    [Fact]
    public void AnIdIsComparedAsWritten_NotDecoded()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs, "id=a%26b")}a{Close} {Open(Docs, "id=a&b")}b{Close}");

        Assert.NotEqual(engine.CellAt(0, 0).Hyperlink, engine.CellAt(2, 0).Hyperlink);
    }

    // ---- caps ----

    [Fact]
    public void AnOverlongUrl_IsNoLinkAtAll()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);
        var url = "https://example.com/" + new string('a', 2100);

        engine.Feed($"{Open(url)}a");

        Assert.True(engine.CellAt(0, 0).Hyperlink.IsNone);
    }

    /// <remarks>
    /// Dropped rather than truncated, and this cap is load-bearing here in a way it is not in
    /// xterm.js: that parser refuses to dispatch an over-long OSC at all, while this one truncates
    /// the payload and dispatches it anyway, so a half-written url would arrive looking well-formed.
    /// </remarks>
    [Fact]
    public void AnOverlongUrl_ClosesAnOpenLinkRatherThanExtendingIt()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Open(Docs)}a{Open("https://example.com/" + new string('a', 2100))}b");

        Assert.Equal(Docs, LinkOf(engine, 0, 0));
        Assert.True(engine.CellAt(1, 0).Hyperlink.IsNone);
    }

    /// <remarks>
    /// The id is a grouping hint, so losing it costs only the grouping — the link itself survives as
    /// an anonymous one. Dropping the whole link would be the worse trade.
    /// </remarks>
    [Fact]
    public void AnOverlongId_FallsBackToAnAnonymousLink()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);
        var id = "id=" + new string('a', 300);

        engine.Feed($"{Open(Docs, id)}a{Close} {Open(Docs, id)}b{Close}");

        Assert.Equal(Docs, LinkOf(engine, 0, 0));
        Assert.NotEqual(engine.CellAt(0, 0).Hyperlink, engine.CellAt(2, 0).Hyperlink);
    }

    // ---- resumability ----

    /// <remarks>
    /// The clause <see cref="ITerminalEngine"/> calls the one worth testing hardest. The id has to
    /// exist before the cells naming it are printed, so this is also what pins the parse to the
    /// parser rather than to anything that reads a finished feed.
    /// </remarks>
    [Fact]
    public void AnOscEightSplitAcrossEveryByte_StillMarksTheSameCells()
    {
        using var whole = EngineUnderTest.Create(columns: 20, rows: 2);
        using var split = EngineUnderTest.Create(columns: 20, rows: 2);
        var session = $"{Open(Docs, "id=x")}docs{Close} plain";

        whole.Feed(session);
        split.FeedByteAtATime(session);

        Assert.Equal(whole.RowText(0), split.RowText(0));
        for (var column = 0; column < 10; column++)
            Assert.Equal(LinkOf(whole, column, 0), LinkOf(split, column, 0));
    }
}
