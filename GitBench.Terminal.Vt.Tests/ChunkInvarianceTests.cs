namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The same bytes must produce the same screen however they are cut up.
/// </summary>
/// <remarks>
/// This needs no golden, which is what makes it worth having: it compares an engine against itself
/// and is therefore true of every correct engine without anyone having to author an expectation.
/// A pseudo-terminal hands over whatever was in the pipe when the read returned, so the split
/// points are real; one byte at a time cuts every escape sequence, every UTF-8 scalar and every
/// OSC string. Resumption bugs are the most likely thing to be wrong in a hand-rolled engine and
/// the hardest to notice, because they only show up under load.
/// </remarks>
public class ChunkInvarianceTests
{
    public static IEnumerable<object[]> EngineCorpusAndChunking() =>
        from engine in TerminalEngines.Names
        from corpus in Corpus.Names
        from chunking in new[] { Chunking.SingleBytes.Name, Chunking.Awkward.Name, Chunking.Whole.Name }
        select new object[] { engine, corpus, chunking };

    [Theory]
    [MemberData(nameof(EngineCorpusAndChunking))]
    public void Replay_InAnyChunkSize_ReachesTheSameScreen(string engineName, string corpusName, string chunkingName)
    {
        var corpus = Corpus.Load(corpusName);

        using var reference = TerminalEngines.Create(engineName, corpus.Size);
        var expected = Replay.Final(reference, corpus, Chunking.PipeSized);

        using var engine = TerminalEngines.Create(engineName, corpus.Size);
        var actual = Replay.Final(engine, corpus, Chunking.ByName(chunkingName));

        var difference = TextDiff.Describe(Body(expected), Body(actual), "4096-byte", $"{chunkingName,9}");
        Assert.True(
            difference is null,
            $"'{corpusName}' replayed in {chunkingName} chunks reached a different screen than the same bytes "
            + "replayed in 4096-byte chunks, so a sequence split across two feeds was not resumed.\n\n"
            + difference);
    }

    /// <summary>The snapshot without its label line, which names the chunking and so always differs.</summary>
    static string Body(GridSnapshot snapshot) => string.Join('\n', snapshot.ToLines().Skip(1));
}
