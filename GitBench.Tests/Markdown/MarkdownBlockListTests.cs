using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using Xunit;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Tests.Markdown;

// The streaming model's contract (Step 7, docs/plans/markdown-renderer.md). The essence pinned
// here is IDENTITY and MINIMAL MUTATION, not rendering (Steps 5-6 pin that):
//
// - Result: after SetText, Blocks element-wise equals the one-shot parse of the same text.
// - Retention: a slot whose parse value did not change keeps its existing MarkdownBlock INSTANCE
//   and fires no ListChange event. The parser allocates fresh (equal-by-value) records on every
//   parse — see TwoParsesOfTheSameTextYieldEqualButDistinctBlockInstances — so the diff must keep
//   the old instance rather than adopt the new parse's. Reference identity is what Each keys view
//   survival on: a slot with no event keeps its view.
// - Minimal tail: every event lands at or after the first divergent index. Common streaming
//   deltas are exactly one Replaced at the last index (block still growing) or one Added (new
//   block opened); retractions remove only the dropped tail slots. Never Cleared/Reset while an
//   equal prefix exists.
// - Throttle: SetTextThrottled coalesces (latest wins, no parse) until Tick applies it with at
//   most one parse; SetText is immediate and clears pending. With a ticker, pending text
//   registers a frame tick and application parks it again (ActiveCount returns to 0), so the
//   frame loop only stays awake while an update is pending.
public class MarkdownBlockListTests
{
    /// <summary>Counts parses so the throttle contract ("at most one parse per tick", "latest
    /// text wins") is observable, delegating to the real parser for results.</summary>
    private sealed class CountingParser : IMarkdownParser
    {
        private readonly BasicMarkdownParser _inner = new();
        public int ParseCount { get; private set; }
        public string? LastText { get; private set; }

        public MarkdownDocument Parse(string text)
        {
            ParseCount++;
            LastText = text;
            return _inner.Parse(text);
        }
    }

    private static MarkdownBlockList NewList() => new(new BasicMarkdownParser());

    /// <summary>Captures every mutation event fired after this call (no synthetic Reset — this
    /// subscribes via the Changed event, not Subscribe).</summary>
    private static List<ListChange<MarkdownBlock>> LogEvents(MarkdownBlockList list)
    {
        var log = new List<ListChange<MarkdownBlock>>();
        list.Blocks.Changed += log.Add;
        return log;
    }

    private static void AssertMatchesOneShotParse(MarkdownBlockList list, string text)
    {
        var expected = new BasicMarkdownParser().Parse(text).Blocks;
        Assert.Equal(expected.Count, list.Blocks.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], list.Blocks[i]);
        }
    }

    // A document exercising several construct kinds at once.
    private const string MixedDocument =
        "# Title\n\nSome **bold** text with `code`.\n\n- one\n- two\n\n> quoted\n\n```csharp\nint x = 1;\n```\n\n|a|b|\n|---|---|\n|1|2|\n\n---\n\nend";

    // ------------------------------------------------------------------ construction & basics

    [Fact]
    public void FreshListHasNoBlocksAndEmptyText()
    {
        var list = NewList();

        Assert.Empty(list.Blocks);
        Assert.Equal(string.Empty, list.Text);
        Assert.False(list.HasPendingText);
    }

    [Fact]
    public void FreshListDoesNotParse()
    {
        var parser = new CountingParser();

        _ = new MarkdownBlockList(parser);

        Assert.Equal(0, parser.ParseCount);
    }

    [Fact]
    public void SetTextPopulatesBlocksMatchingTheOneShotParse()
    {
        var list = NewList();

        list.SetText(MixedDocument);

        AssertMatchesOneShotParse(list, MixedDocument);
    }

    [Fact]
    public void SetTextParsesExactlyOnceImmediately()
    {
        var parser = new CountingParser();
        var list = new MarkdownBlockList(parser);

        list.SetText("hello **world**");

        Assert.Equal(1, parser.ParseCount);
        Assert.Equal("hello **world**", parser.LastText);
    }

    [Fact]
    public void SetTextUpdatesTheTextProperty()
    {
        var list = NewList();

        list.SetText("alpha");

        Assert.Equal("alpha", list.Text);
    }

    [Fact]
    public void SetTextWithEmptyTextOnFreshListFiresNoEvents()
    {
        var list = NewList();
        var log = LogEvents(list);

        list.SetText(string.Empty);

        Assert.Empty(log);
        Assert.Empty(list.Blocks);
    }

    // ------------------------------------------------------- retention & minimal mutation

    [Fact]
    public void TwoParsesOfTheSameTextYieldEqualButDistinctBlockInstances()
    {
        // Sanity for the whole identity contract: value equality across parses is NOT reference
        // identity. If this ever fails (a memoizing parser), the retention tests below lose their
        // teeth — but the contract they pin stays valid.
        var parser = new BasicMarkdownParser();

        var first = parser.Parse("# Head\n\npara").Blocks;
        var second = parser.Parse("# Head\n\npara").Blocks;

        Assert.Equal(first[0], second[0]);
        Assert.NotSame(first[0], second[0]);
    }

    [Fact]
    public void ReparsingEqualTextFiresNoEventsAndKeepsInstances()
    {
        var list = NewList();
        list.SetText("# Head\n\npara");
        var heading = list.Blocks[0];
        var paragraph = list.Blocks[1];
        var log = LogEvents(list);

        list.SetText("# Head\n\npara");

        Assert.Empty(log);
        Assert.Same(heading, list.Blocks[0]);
        Assert.Same(paragraph, list.Blocks[1]);
    }

    [Fact]
    public void AppendToTheLastParagraphFiresExactlyOneReplaceAtTheLastIndex()
    {
        var list = NewList();
        list.SetText("# Head\n\npara");
        var log = LogEvents(list);

        list.SetText("# Head\n\npara grows");

        var change = Assert.Single(log);
        Assert.Equal(ListChangeKind.Replaced, change.Kind);
        Assert.Equal(1, change.Index);
    }

    [Fact]
    public void AppendToTheLastParagraphRetainsEveryEarlierInstance()
    {
        var list = NewList();
        list.SetText("# Head\n\nfirst para\n\nsecond");
        var block0 = list.Blocks[0];
        var block1 = list.Blocks[1];

        list.SetText("# Head\n\nfirst para\n\nsecond grows");

        Assert.Same(block0, list.Blocks[0]);
        Assert.Same(block1, list.Blocks[1]);
        AssertMatchesOneShotParse(list, "# Head\n\nfirst para\n\nsecond grows");
    }

    [Fact]
    public void ReplacedTailSlotHoldsTheNewValue()
    {
        var list = NewList();
        list.SetText("para");

        list.SetText("para grows");

        var expected = new BasicMarkdownParser().Parse("para grows").Blocks[0];
        Assert.Equal(expected, list.Blocks[0]);
    }

    [Fact]
    public void AppendThatOpensANewBlockFiresExactlyOneAddAndLeavesThePrefixUntouched()
    {
        var list = NewList();
        list.SetText("alpha");
        var block0 = list.Blocks[0];
        var log = LogEvents(list);

        list.SetText("alpha\n\n# Next");

        var change = Assert.Single(log);
        Assert.Equal(ListChangeKind.Added, change.Kind);
        Assert.Equal(1, change.Index);
        Assert.Same(block0, list.Blocks[0]);
        Assert.Equal(2, list.Blocks.Count);
    }

    [Fact]
    public void StreamedParagraphIdentityStabilizesOnceItsValueStopsChanging()
    {
        // Once block 0's text is final ("alpha" from the moment the blank line lands), its
        // instance must never churn again while the tail streams — monotone identity for the
        // completed prefix.
        var list = NewList();
        list.SetText("al");
        list.SetText("alpha");
        list.SetText("alpha\n\nbe");
        var completed = list.Blocks[0];

        list.SetText("alpha\n\nbeta");
        Assert.Same(completed, list.Blocks[0]);

        list.SetText("alpha\n\nbeta continues here");
        Assert.Same(completed, list.Blocks[0]);

        list.SetText("alpha\n\nbeta continues here\n\n- and\n- a list");
        Assert.Same(completed, list.Blocks[0]);
    }

    [Fact]
    public void ChangingAMiddleBlockReplacesOnlyThatSlot()
    {
        // Slots after the change whose value is unchanged at the SAME index must also stay
        // untouched — the diff compares per index, and ObservableList.Replace already no-ops on
        // equal values, so an equal slot must never be removed/re-added around.
        var list = NewList();
        list.SetText("aaa\n\nbbb\n\nccc");
        var block0 = list.Blocks[0];
        var block2 = list.Blocks[2];
        var log = LogEvents(list);

        list.SetText("aaa\n\nBBB\n\nccc");

        var change = Assert.Single(log);
        Assert.Equal(ListChangeKind.Replaced, change.Kind);
        Assert.Equal(1, change.Index);
        Assert.Same(block0, list.Blocks[0]);
        Assert.Same(block2, list.Blocks[2]);
    }

    [Fact]
    public void TypeChangeReplacesTheSlotInPlace()
    {
        var list = NewList();
        list.SetText("text");
        var log = LogEvents(list);

        list.SetText("# text");

        var change = Assert.Single(log);
        Assert.Equal(ListChangeKind.Replaced, change.Kind);
        Assert.Equal(0, change.Index);
        Assert.IsType<HeadingBlock>(list.Blocks[0]);
    }

    [Fact]
    public void ShrinkingTextRemovesOnlyTheRetractedTailSlots()
    {
        var list = NewList();
        list.SetText("aaa\n\nbbb\n\nccc");
        var block0 = list.Blocks[0];
        var log = LogEvents(list);

        list.SetText("aaa");

        Assert.Equal(2, log.Count);
        Assert.All(log, c => Assert.Equal(ListChangeKind.Removed, c.Kind));
        Assert.All(log, c => Assert.True(c.Index >= 1,
            $"a removal touched index {c.Index}, inside the unchanged prefix"));
        Assert.Same(block0, Assert.Single(list.Blocks));
    }

    [Fact]
    public void ShrinkToEmptyEmptiesTheList()
    {
        var list = NewList();
        list.SetText("aaa\n\nbbb");

        list.SetText(string.Empty);

        Assert.Empty(list.Blocks);
        Assert.Equal(string.Empty, list.Text);
    }

    // ------------------------------------------------------------------ open fence streaming

    [Fact]
    public void OpenFenceStreamsAsALiveCodeBlock()
    {
        var list = NewList();

        list.SetText("intro\n\n```csharp");

        Assert.Equal(2, list.Blocks.Count);
        var code = Assert.IsType<CodeBlock>(list.Blocks[1]);
        Assert.Equal("csharp", code.Language);
        Assert.Equal(string.Empty, code.Text);
        Assert.False(code.IsClosed);
    }

    [Fact]
    public void GrowingOpenFenceReplacesTheCodeSlotInPlace()
    {
        var list = NewList();
        list.SetText("intro\n\n```csharp\nint x = 1;");
        var intro = list.Blocks[0];
        var log = LogEvents(list);

        list.SetText("intro\n\n```csharp\nint x = 1;\nint y = 2;");

        var change = Assert.Single(log);
        Assert.Equal(ListChangeKind.Replaced, change.Kind);
        Assert.Equal(1, change.Index);
        var code = Assert.IsType<CodeBlock>(list.Blocks[1]);
        Assert.Equal("int x = 1;\nint y = 2;", code.Text);
        Assert.False(code.IsClosed);
        Assert.Same(intro, list.Blocks[0]);
    }

    [Fact]
    public void ClosingFenceFlipsIsClosedInTheSameSlot()
    {
        var list = NewList();
        list.SetText("intro\n\n```csharp\nint x = 1;");
        var intro = list.Blocks[0];
        var log = LogEvents(list);

        list.SetText("intro\n\n```csharp\nint x = 1;\n```");

        var change = Assert.Single(log);
        Assert.Equal(ListChangeKind.Replaced, change.Kind);
        Assert.Equal(1, change.Index);
        var code = Assert.IsType<CodeBlock>(list.Blocks[1]);
        Assert.True(code.IsClosed);
        Assert.Equal("int x = 1;", code.Text);
        Assert.Same(intro, list.Blocks[0]);
    }

    // ------------------------------------------------------------------------ table streaming

    [Fact]
    public void HeaderAndDelimiterAloneStreamAsATable()
    {
        var list = NewList();

        list.SetText("|Name|Value|\n|---|---|");

        var table = Assert.IsType<TableBlock>(Assert.Single(list.Blocks));
        Assert.Equal(2, table.Columns.Count);
        Assert.Empty(table.Rows);
    }

    [Fact]
    public void TableRowArrivalReplacesTheTableSlotInPlace()
    {
        var list = NewList();
        list.SetText("|Name|Value|\n|---|---|");
        var log = LogEvents(list);

        list.SetText("|Name|Value|\n|---|---|\n|alpha|1|");

        var change = Assert.Single(log);
        Assert.Equal(ListChangeKind.Replaced, change.Kind);
        Assert.Equal(0, change.Index);
        var table = Assert.IsType<TableBlock>(list.Blocks[0]);
        Assert.Single(table.Rows);
    }

    // ------------------------------------------------------------------------------- replay

    // A recorded-response-shaped fixture exercising every supported construct; the replay tests
    // feed every prefix of it, which crosses every construct's in-progress states.
    private const string RecordedResponse =
        "# Release notes\n" +
        "\n" +
        "Here is **bold**, *italic*, `code`, ~~gone~~, and a [link](https://example.com/a).\n" +
        "Auto https://example.com/b here.  \n" +
        "After a hard break.\n" +
        "\n" +
        "## Changes\n" +
        "\n" +
        "- first item\n" +
        "- second item\n" +
        "  - nested item\n" +
        "- [x] shipped task\n" +
        "- [ ] open task\n" +
        "\n" +
        "3. ordered one\n" +
        "4. ordered two\n" +
        "\n" +
        "> quoted wisdom\n" +
        "> > nested quote\n" +
        "\n" +
        "```csharp\n" +
        "int count = 42; // the answer\n" +
        "string s = \"done\";\n" +
        "```\n" +
        "\n" +
        "| Name | Value |\n" +
        "|:-----|------:|\n" +
        "| alpha | 1 |\n" +
        "| beta | 2 |\n" +
        "\n" +
        "---\n" +
        "\n" +
        "Closing paragraph.\n";

    [Fact]
    public void ReplayEveryPrefixNeverThrowsAndMatchesTheOneShotParse()
    {
        var list = NewList();
        var oneShot = new BasicMarkdownParser();

        for (var len = 0; len <= RecordedResponse.Length; len++)
        {
            var prefix = RecordedResponse[..len];
            list.SetText(prefix);

            var expected = oneShot.Parse(prefix).Blocks;
            Assert.Equal(expected.Count, list.Blocks.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i], list.Blocks[i]);
            }
        }
    }

    [Fact]
    public void ReplayRetainsInstancesForEverySlotWhoseValueIsUnchanged()
    {
        // The per-step encoding of monotone identity: whenever a slot's value survives a step,
        // its instance must too. Chained over all steps this means a completed block's instance
        // never churns while only the tail grows.
        var list = NewList();

        for (var len = 0; len <= RecordedResponse.Length; len++)
        {
            var before = list.Blocks.ToArray();
            list.SetText(RecordedResponse[..len]);

            var shared = Math.Min(before.Length, list.Blocks.Count);
            for (var i = 0; i < shared; i++)
            {
                if (Equals(before[i], list.Blocks[i]))
                {
                    Assert.Same(before[i], list.Blocks[i]);
                }
            }
        }
    }

    [Fact]
    public void ReplayMutatesOnlyFromTheFirstDivergentIndexOnward()
    {
        var oneShot = new BasicMarkdownParser();
        var list = NewList();
        var log = LogEvents(list);

        for (var len = 0; len <= RecordedResponse.Length; len++)
        {
            var prefix = RecordedResponse[..len];
            var before = list.Blocks.ToArray();
            var expected = oneShot.Parse(prefix).Blocks;
            var divergence = FirstDivergence(before, expected);

            log.Clear();
            list.SetText(prefix);

            foreach (var change in log)
            {
                Assert.NotEqual(ListChangeKind.Reset, change.Kind);
                if (change.Kind == ListChangeKind.Cleared)
                {
                    // Wiping the list is only ever legitimate when nothing survives.
                    Assert.Equal(0, divergence);
                    continue;
                }
                Assert.True(change.Index >= divergence,
                    $"prefix length {len}: a {change.Kind} event touched index {change.Index}, " +
                    $"inside the unchanged prefix [0, {divergence})");
            }
        }
    }

    [Fact]
    public void ReplayFinalStateEqualsTheOneShotParseOfTheFullText()
    {
        var list = NewList();

        for (var len = 0; len <= RecordedResponse.Length; len++)
        {
            list.SetText(RecordedResponse[..len]);
        }

        AssertMatchesOneShotParse(list, RecordedResponse);
    }

    /// <summary>First index where the old sequence and the new parse disagree; when they are
    /// element-wise equal, <see cref="int.MaxValue"/> (no event is allowed at all).</summary>
    private static int FirstDivergence(
        IReadOnlyList<MarkdownBlock> before, IReadOnlyList<MarkdownBlock> after)
    {
        var shared = Math.Min(before.Count, after.Count);
        for (var i = 0; i < shared; i++)
        {
            if (!Equals(before[i], after[i])) return i;
        }
        return before.Count == after.Count ? int.MaxValue : shared;
    }

    // ------------------------------------------------------------------------------ throttle

    [Fact]
    public void ThrottledTextDoesNotApplyBeforeTick()
    {
        var parser = new CountingParser();
        var list = new MarkdownBlockList(parser);

        list.SetTextThrottled("pending para");

        Assert.Equal(0, parser.ParseCount);
        Assert.Empty(list.Blocks);
        Assert.True(list.HasPendingText);
        Assert.Equal(string.Empty, list.Text);
    }

    [Fact]
    public void TickAppliesThePendingTextWithASingleParse()
    {
        var parser = new CountingParser();
        var list = new MarkdownBlockList(parser);
        list.SetTextThrottled("hello");

        list.Tick();

        Assert.Equal(1, parser.ParseCount);
        Assert.Equal("hello", parser.LastText);
        Assert.Equal("hello", list.Text);
        Assert.False(list.HasPendingText);
        AssertMatchesOneShotParse(list, "hello");
    }

    [Fact]
    public void LatestThrottledTextWinsOnTick()
    {
        var parser = new CountingParser();
        var list = new MarkdownBlockList(parser);
        list.SetTextThrottled("# one");
        list.SetTextThrottled("# one\n\ntwo");

        list.Tick();

        Assert.Equal(1, parser.ParseCount);
        Assert.Equal("# one\n\ntwo", parser.LastText);
        AssertMatchesOneShotParse(list, "# one\n\ntwo");
    }

    [Fact]
    public void TickWithoutPendingTextDoesNothing()
    {
        var parser = new CountingParser();
        var list = new MarkdownBlockList(parser);
        var log = LogEvents(list);

        list.Tick();

        Assert.Equal(0, parser.ParseCount);
        Assert.Empty(log);
    }

    [Fact]
    public void SecondTickAfterApplicationIsANoOp()
    {
        var parser = new CountingParser();
        var list = new MarkdownBlockList(parser);
        list.SetTextThrottled("hello");
        list.Tick();

        list.Tick();

        Assert.Equal(1, parser.ParseCount);
    }

    [Fact]
    public void SetTextBypassesTheThrottleAndClearsPendingText()
    {
        var parser = new CountingParser();
        var list = new MarkdownBlockList(parser);
        list.SetTextThrottled("# stale");

        list.SetText("# fresh");

        Assert.Equal(1, parser.ParseCount);
        Assert.Equal("# fresh", parser.LastText);
        Assert.False(list.HasPendingText);

        // The superseded throttled text must never resurface.
        list.Tick();
        Assert.Equal(1, parser.ParseCount);
        AssertMatchesOneShotParse(list, "# fresh");
    }

    [Fact]
    public void ThrottledPathRoutesThroughTheSameMinimalDiff()
    {
        var list = NewList();
        list.SetText("# Head\n\npara");
        var heading = list.Blocks[0];
        var log = LogEvents(list);

        list.SetTextThrottled("# Head\n\npara grows");
        Assert.Empty(log);
        list.Tick();

        var change = Assert.Single(log);
        Assert.Equal(ListChangeKind.Replaced, change.Kind);
        Assert.Equal(1, change.Index);
        Assert.Same(heading, list.Blocks[0]);
    }

    // ------------------------------------------------------------------- ticker integration

    [Fact]
    public void PendingThrottledTextWakesTheTickerAndApplicationParksIt()
    {
        // The Pulse/Tween convention: register a frame tick only while there is work, unregister
        // once done — ActiveCount 0 is how the app (and Settle) knows the UI is at rest.
        var ticker = new FrameTicker();
        var parser = new CountingParser();
        var list = new MarkdownBlockList(parser, ticker);
        Assert.Equal(0, ticker.ActiveCount);

        list.SetTextThrottled("hello");
        Assert.True(ticker.ActiveCount > 0,
            "pending throttled text must register a frame tick to apply itself");

        ticker.Tick(1f / 30f);

        Assert.Equal(1, parser.ParseCount);
        Assert.Equal("hello", list.Text);
        AssertMatchesOneShotParse(list, "hello");
        Assert.Equal(0, ticker.ActiveCount);
    }

    [Fact]
    public void TickerDrivenListAppliesEachThrottledUpdateOnTheNextFrame()
    {
        var ticker = new FrameTicker();
        var parser = new CountingParser();
        var list = new MarkdownBlockList(parser, ticker);

        list.SetTextThrottled("first");
        ticker.Tick(1f / 30f);
        Assert.Equal("first", list.Text);

        list.SetTextThrottled("first grows");
        Assert.True(ticker.ActiveCount > 0, "a second pending update must re-register");
        ticker.Tick(1f / 30f);

        Assert.Equal("first grows", list.Text);
        Assert.Equal(2, parser.ParseCount);
        Assert.Equal(0, ticker.ActiveCount);
    }

    [Fact]
    public void CoalescedUpdatesBetweenFramesParseOnceOnTheNextFrame()
    {
        var ticker = new FrameTicker();
        var parser = new CountingParser();
        var list = new MarkdownBlockList(parser, ticker);

        list.SetTextThrottled("a");
        list.SetTextThrottled("ab");
        list.SetTextThrottled("abc");
        ticker.Tick(1f / 30f);

        Assert.Equal(1, parser.ParseCount);
        Assert.Equal("abc", parser.LastText);
    }

    [Fact]
    public void ImmediateSetTextDoesNotWakeTheTicker()
    {
        var ticker = new FrameTicker();
        var list = new MarkdownBlockList(new BasicMarkdownParser(), ticker);

        list.SetText("hello");

        Assert.Equal(0, ticker.ActiveCount);
    }
}
