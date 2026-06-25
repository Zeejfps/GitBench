using GitBench.Controls;
using Xunit;
using ZGF.Gui;

namespace GitBench.Tests;

public class TweenTests
{
    [Fact]
    public void Play_AdvancesProgressToOne_ThenStops()
    {
        var ticker = new FrameTicker();
        var tween = new Tween(ticker, durationSeconds: 1f, Easings.Linear);

        Assert.Equal(0f, tween.Progress.Value);

        tween.Play();
        ticker.Tick(0.5f);
        Assert.True(Math.Abs(tween.Progress.Value - 0.5f) < 0.001f, $"after 0.5s expected ~0.5 but was {tween.Progress.Value}");

        ticker.Tick(0.5f);
        Assert.True(Math.Abs(tween.Progress.Value - 1f) < 0.001f, $"after 1s expected 1 but was {tween.Progress.Value}");

        // Past the end: clamped at 1, and the tick has unregistered itself so further ticks are no-ops.
        ticker.Tick(1f);
        Assert.True(Math.Abs(tween.Progress.Value - 1f) < 0.001f);
    }

    [Fact]
    public void Reverse_RunsProgressBackToZero()
    {
        var ticker = new FrameTicker();
        var tween = new Tween(ticker, durationSeconds: 1f, Easings.Linear);

        tween.Play();
        ticker.Tick(1f);
        Assert.True(Math.Abs(tween.Progress.Value - 1f) < 0.001f);

        tween.Reverse();
        ticker.Tick(1f);
        Assert.True(Math.Abs(tween.Progress.Value) < 0.001f, $"after reverse expected 0 but was {tween.Progress.Value}");
    }

    [Fact]
    public void Restart_SnapsToZero_AndPlaysForwardAgain()
    {
        var ticker = new FrameTicker();
        var tween = new Tween(ticker, durationSeconds: 1f, Easings.Linear);

        tween.Play();
        ticker.Tick(1f);
        Assert.True(Math.Abs(tween.Progress.Value - 1f) < 0.001f);

        tween.Restart();
        Assert.Equal(0f, tween.Progress.Value);

        ticker.Tick(0.5f);
        Assert.True(Math.Abs(tween.Progress.Value - 0.5f) < 0.001f, $"after restart + 0.5s expected ~0.5 but was {tween.Progress.Value}");
    }

    [Fact]
    public void Completed_FiresOnce_WhenReachingEnd()
    {
        var ticker = new FrameTicker();
        var tween = new Tween(ticker, durationSeconds: 1f, Easings.Linear);
        var count = 0;
        tween.Completed += () => count++;

        tween.Play();
        ticker.Tick(1f);
        ticker.Tick(1f);

        Assert.Equal(1, count);
    }
}
