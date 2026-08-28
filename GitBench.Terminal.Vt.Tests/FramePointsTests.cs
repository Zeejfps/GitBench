
namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The scanner that decides where a corpus is snapshotted. Every golden's frame boundaries rest on
/// it, so if it silently found the wrong offsets the goldens would assert about the wrong moments
/// and still be green. Pinned in its own right, like the corpora it reads.
/// </summary>
public class FramePointsTests
{
    const string Esc = Vt.Esc;

    // ------------------------------------------------------------ what a stream declares itself

    [Fact]
    public void Declared_MarksTheByteAfterASynchronizedOutputClose()
    {
        var stream = $"{Esc}[?2026hpainting{Esc}[?2026l";

        var point = Assert.Single(FramePoints.Declared(Bytes(stream)));

        Assert.Equal(new FramePoint(stream.Length, FrameReason.SyncFrameClose), point);
    }

    [Fact]
    public void Declared_DoesNotMarkTheOpeningOfASynchronizedBlock()
    {
        Assert.Empty(FramePoints.Declared(Bytes($"{Esc}[?2026hstill painting")));
    }

    [Theory]
    [InlineData("[?1049h", FrameReason.AltScreenEnter)]
    [InlineData("[?1049l", FrameReason.AltScreenLeave)]
    public void Declared_MarksAlternateScreenTransitions(string sequence, FrameReason expected)
    {
        var point = Assert.Single(FramePoints.Declared(Bytes(Esc + sequence)));

        Assert.Equal(expected, point.Reason);
    }

    [Fact]
    public void Declared_ReadsModesOutOfAMultiParameterSet()
    {
        var point = Assert.Single(FramePoints.Declared(Bytes($"{Esc}[?1006;1049;2004h")));

        Assert.Equal(FrameReason.AltScreenEnter, point.Reason);
    }

    [Fact]
    public void Declared_IgnoresASequenceSpelledInsideAnOperatingSystemCommand()
    {
        // A window title is arbitrary text. A scanner that searched for bytes would find this one.
        Assert.Empty(FramePoints.Declared(Bytes($"{Esc}]0;{Esc}[?2026l is not a sequence")));
    }

    [Fact]
    public void Declared_IgnoresANonPrivateModeReset()
    {
        // CSI 2026 l without the '?' is ANSI mode 2026, not the DEC private mode.
        Assert.Empty(FramePoints.Declared(Bytes($"{Esc}[2026l")));
    }

    // ------------------------------------------------------------ what the suite adds on top

    [Fact]
    public void Find_AlwaysEndsAtTheEndOfTheStream()
    {
        var points = FramePoints.Find(Bytes("hello"));

        Assert.Equal(new FramePoint(5, FrameReason.EndOfStream), points[^1]);
    }

    [Fact]
    public void Find_SamplesAStreamThatDeclaresNoFramesOfItsOwn()
    {
        // A pager draws its whole session between entering and leaving the alternate screen. Left
        // to its own boundaries a golden of one would hold nothing but empty screens.
        var points = FramePoints.Find(Bytes(new string('x', 700)));

        Assert.Equal(FramePoints.IntervalSamples, points.Count(point => point.Reason == FrameReason.Interval));
        Assert.All(points, point => Assert.InRange(point.Offset, 1, 700));
    }

    [Fact]
    public void Find_LeavesAStreamAloneOnceItDeclaresEnoughFramesItself()
    {
        var declared = string.Concat(Enumerable.Repeat($"{Esc}[?2026h.{Esc}[?2026l", FramePoints.MinimumDeclaredFrames));

        var points = FramePoints.Find(Bytes(declared));

        Assert.DoesNotContain(points, point => point.Reason == FrameReason.Interval);
    }

    [Fact]
    public void Find_CollapsesTwoPointsThatLandOnTheSameByte()
    {
        var points = FramePoints.Find(Bytes($"{Esc}[?2026l"));

        Assert.Single(points, point => point.Offset == 8);
    }

    [Fact]
    public void Declared_OnTheClaudeCorpus_FindsOneFramePerSynchronizedBlock()
    {
        var corpus = Corpus.Load("claude");

        var syncFrames = FramePoints.Declared(corpus.Bytes).Count(point => point.Reason == FrameReason.SyncFrameClose);

        // The recorded inventory counts twenty '?2026l' resets in this session.
        Assert.Equal(20, syncFrames);
    }

    static byte[] Bytes(string text) => Vt.Bytes(text);
}
