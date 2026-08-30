namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// Replays a whole recorded session and compares the screen against a committed golden.
/// </summary>
/// <remarks>
/// <para>
/// A golden holds several frames, taken where the recorded program itself declared a frame was
/// finished — the close of a synchronized-output block, or an alternate-screen switch. Snapshotting
/// only the final screen would test a fraction of the stream: most of what a TUI does is overwrite
/// what it drew a moment ago, and a bug that draws the wrong thing and then corrects it is
/// invisible at the end. The offsets come from scanning the corpus, never from the engine, so one
/// missed frame cannot shift every later frame in the golden.
/// </para>
/// <para>
/// The golden is not keyed by engine. That is the contract: two engines that both emulate a
/// terminal correctly produce the same screen from the same bytes, and any golden that needed a
/// per-engine variant would be recording an implementation rather than a terminal.
/// </para>
/// <para>
/// The claude corpus is the acceptance case and was the last to get a golden, because it could not
/// have one until the engine could replay it: truecolor threw out of <c>Terminal.MatchColor</c>
/// until patch 1. It was hand-audited like the others — its <c>;</c> block says which frames were
/// read cell by cell and which were only checked structurally, so the next person knows what the
/// file is worth.
/// </para>
/// </remarks>
public class CorpusReplayTests
{
    public static IEnumerable<object[]> EngineAndCorpus() =>
        from engine in TerminalEngines.Names
        from corpus in Corpus.Names
        select new object[] { engine, corpus };

    [Theory]
    [MemberData(nameof(EngineAndCorpus))]
    public void Replay_ProducesTheGoldenScreens(string engineName, string corpusName)
    {
        var corpus = Corpus.Load(corpusName);
        using var engine = TerminalEngines.Create(engineName, corpus.Size);

        var frames = Replay.Frames(engine, corpus, FramePoints.Find(corpus.Bytes), Chunking.PipeSized);

        GoldenFile.Matches($"{corpusName}.grid", string.Join('\n', frames.Select(frame => frame.ToText())));
    }

    [Theory]
    [MemberData(nameof(TerminalEngines.All), MemberType = typeof(TerminalEngines))]
    public void Replay_OfTheAcceptanceCorpus_CountsOneCompletedFramePerSynchronizedBlock(string engineName)
    {
        var corpus = Corpus.Load("claude");
        using var engine = TerminalEngines.Create(engineName, corpus.Size);
        var expected = FramePoints.Find(corpus.Bytes).Count(point => point.Reason == FrameReason.SyncFrameClose);

        var completed = 0;
        foreach (var chunk in Chunks(corpus.Bytes, 512))
            completed += engine.Feed(chunk.Span).FramesCompleted;

        Assert.Equal(expected, completed);
    }

    static IEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] bytes, int size)
    {
        for (var offset = 0; offset < bytes.Length; offset += size)
            yield return bytes.AsMemory(offset, Math.Min(size, bytes.Length - offset));
    }
}
