

namespace GitBench.Terminal.Vt.Tests;

/// <summary>How a corpus is handed to the engine — the sizes of the chunks it arrives in.</summary>
/// <remarks>
/// A pseudo-terminal delivers whatever happened to be in the pipe, so every corpus is replayed at
/// several chunk sizes and must produce the same screen. One byte at a time is the cruel case: it
/// splits every escape sequence, every UTF-8 scalar and every OSC string across calls.
/// </remarks>
public readonly record struct Chunking(string Name, int Size)
{
    public static readonly Chunking Whole = new("whole", int.MaxValue);
    public static readonly Chunking SingleBytes = new("1-byte", 1);
    public static readonly Chunking Awkward = new("7-byte", 7);
    public static readonly Chunking PipeSized = new("4096-byte", 4096);

    public static Chunking ByName(string name) =>
        new[] { Whole, SingleBytes, Awkward, PipeSized }.Single(chunking => chunking.Name == name);
}

/// <summary>Feeds a corpus to an engine and captures the frames a golden is made of.</summary>
public static class Replay
{
    /// <summary>Feeds the whole corpus, snapshotting at each of <paramref name="points"/>.</summary>
    public static IReadOnlyList<GridSnapshot> Frames(
        ITerminalEngine engine,
        Corpus corpus,
        IReadOnlyList<FramePoint> points,
        Chunking chunking)
    {
        var frames = new List<GridSnapshot>();
        var fed = 0;

        for (var i = 0; i < points.Count; i++)
        {
            FeedTo(engine, corpus.Bytes, ref fed, points[i].Offset, chunking);
            frames.Add(GridSnapshot.Capture(
                engine,
                $"{corpus.Name}  frame {i + 1}/{points.Count}  after {points[i].Offset}/{corpus.Bytes.Length} bytes"
                + $"  ({Describe(points[i].Reason)})"));
        }

        return frames;
    }

    /// <summary>Feeds the whole corpus and captures only the final screen.</summary>
    public static GridSnapshot Final(ITerminalEngine engine, Corpus corpus, Chunking chunking)
    {
        var fed = 0;
        FeedTo(engine, corpus.Bytes, ref fed, corpus.Bytes.Length, chunking);
        return GridSnapshot.Capture(engine, $"{corpus.Name}  final  ({corpus.Bytes.Length} bytes, {chunking.Name} chunks)");
    }

    static void FeedTo(ITerminalEngine engine, byte[] bytes, ref int fed, int target, Chunking chunking)
    {
        while (fed < target)
        {
            var take = Math.Min(chunking.Size, target - fed);
            engine.Feed(bytes.AsSpan(fed, take));
            fed += take;
        }
    }

    static string Describe(FrameReason reason) => reason switch
    {
        FrameReason.SyncFrameClose => "sync-frame close",
        FrameReason.AltScreenEnter => "alt screen enter",
        FrameReason.AltScreenLeave => "alt screen leave",
        FrameReason.Interval => "interval sample",
        FrameReason.EndOfStream => "end of stream",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
