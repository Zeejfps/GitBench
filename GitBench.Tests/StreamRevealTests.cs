using GitBench.Features.Pairing;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

public class StreamRevealTests
{
    private readonly ManualTicker _ticker = new();

    [Fact]
    public void WhatWasWrittenBeforeIsShownAtOnce()
    {
        var source = new State<string>("Hello there.");
        using var reveal = new StreamReveal(source, _ticker, fromStart: false);

        Assert.Equal("Hello there.", reveal.Shown.Value);
    }

    [Fact]
    public void ANewChunkIsRevealedOverSeveralFrames()
    {
        var source = new State<string>("");
        using var reveal = new StreamReveal(source, _ticker, fromStart: true);

        source.Value = new string('a', 200);
        _ticker.Tick();

        Assert.InRange(reveal.Shown.Value.Length, 1, 199);
        for (var i = 0; i < 90; i++) _ticker.Tick();
        Assert.Equal(source.Value, reveal.Shown.Value);
    }

    [Fact]
    public void TextRewrittenBehindTheRevealIsFollowed()
    {
        var source = new State<string>("Done.  ");
        using var reveal = new StreamReveal(source, _ticker, fromStart: false);

        source.Value = "Done.\n\nNext.";
        for (var i = 0; i < 60; i++) _ticker.Tick();

        Assert.Equal("Done.\n\nNext.", reveal.Shown.Value);
    }
}
