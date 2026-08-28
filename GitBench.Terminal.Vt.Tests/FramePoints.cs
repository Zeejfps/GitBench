namespace GitBench.Terminal.Vt.Tests;

/// <summary>Why a corpus is worth snapshotting at a particular byte offset.</summary>
public enum FrameReason
{
    /// <summary>A synchronized-output block closed (DEC mode 2026 reset): the program says the frame is complete.</summary>
    SyncFrameClose,

    /// <summary>The program switched to the alternate screen (?1049h).</summary>
    AltScreenEnter,

    /// <summary>The program left the alternate screen (?1049l).</summary>
    AltScreenLeave,

    /// <summary>
    /// An evenly spaced sample. Added only to streams that declare almost no frames of their own,
    /// so that something is captured while the program is actually drawing.
    /// </summary>
    Interval,

    /// <summary>Every byte in the corpus has been fed.</summary>
    EndOfStream,
}

/// <param name="Offset">The number of bytes that must be fed to reach this point.</param>
public readonly record struct FramePoint(int Offset, FrameReason Reason);

/// <summary>
/// Finds the byte offsets in a recorded stream that are worth snapshotting.
/// </summary>
/// <remarks>
/// <para>
/// The offsets come from scanning the corpus, never from asking the engine. If snapshot points
/// were taken from <see cref="FeedResult.FramesCompleted"/>, an engine that missed one frame
/// would shift every later frame in the golden and drown one real divergence in twenty false ones.
/// Byte offsets are the same for every engine and can be found by hand in a hex dump.
/// </para>
/// <para>
/// A synchronized-output close is the natural boundary: it is the program declaring that what is
/// on screen is a complete picture. Alternate-screen transitions are the other natural boundary,
/// for corpora that never use mode 2026.
/// </para>
/// <para>
/// Those two are not always enough. A pager declares nothing at all between entering the alternate
/// screen and leaving it, so a golden built from its own boundaries holds an empty screen, an empty
/// screen and an empty screen while everything interesting happened in between. When a stream
/// declares fewer than <see cref="MinimumDeclaredFrames"/> frames of its own, evenly spaced samples
/// are added. Those land mid-sequence, which is intentional: an engine has to be resumable there
/// anyway, and the state it holds mid-sequence is worth pinning.
/// </para>
/// <para>
/// This is a proper little state machine rather than a byte search because a title or a hyperlink
/// can legitimately contain the text of an escape sequence, and a search would find it.
/// </para>
/// </remarks>
public static class FramePoints
{
    /// <summary>
    /// Every point worth snapshotting: what the stream declared, topped up with evenly spaced
    /// samples when it declared almost nothing, and always ending at the end of the stream.
    /// </summary>
    public static IReadOnlyList<FramePoint> Find(ReadOnlySpan<byte> stream)
    {
        var points = Declared(stream).ToList();

        if (points.Count < MinimumDeclaredFrames)
        {
            for (var sample = 1; sample <= IntervalSamples; sample++)
            {
                // An offset of zero would snapshot a screen nothing has been fed to, and the end
                // of the stream already has its own point.
                var offset = stream.Length * sample / (IntervalSamples + 1);
                if (offset > 0 && offset < stream.Length)
                    points.Add(new FramePoint(offset, FrameReason.Interval));
            }
        }

        points.Add(new FramePoint(stream.Length, FrameReason.EndOfStream));

        return points
            .GroupBy(point => point.Offset)
            .OrderBy(group => group.Key)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>Only the frames the stream declared for itself, in the order it declared them.</summary>
    public static IReadOnlyList<FramePoint> Declared(ReadOnlySpan<byte> stream)
    {
        var points = new List<FramePoint>();
        var state = State.Ground;
        var sequence = new List<byte>();

        for (var i = 0; i < stream.Length; i++)
        {
            var b = stream[i];

            switch (state)
            {
                case State.Ground:
                    if (b == Escape)
                        state = State.Escape;
                    break;

                case State.Escape:
                    state = (char)b switch
                    {
                        '[' => State.ControlSequence,
                        ']' or 'P' or '_' or '^' or 'X' => State.String,
                        _ => State.Ground,
                    };
                    sequence.Clear();
                    break;

                case State.ControlSequence:
                    if (b is >= 0x20 and <= 0x3F)
                    {
                        sequence.Add(b);
                        break;
                    }

                    if (b is >= 0x40 and <= 0x7E)
                    {
                        var reason = Classify(sequence, (char)b);
                        if (reason is not null)
                            points.Add(new FramePoint(i + 1, reason.Value));
                    }

                    state = State.Ground;
                    break;

                case State.String:
                    // BEL, or ST written as ESC backslash.
                    if (b == Bell)
                        state = State.Ground;
                    else if (b == Escape && i + 1 < stream.Length && stream[i + 1] == (byte)'\\')
                    {
                        i++;
                        state = State.Ground;
                    }

                    break;
            }
        }

        return points;
    }

    /// <summary>Below this many self-declared frames, a stream gets evenly spaced samples too.</summary>
    public const int MinimumDeclaredFrames = 4;

    public const int IntervalSamples = 6;

    const byte Escape = 0x1B;
    const byte Bell = 0x07;

    enum State
    {
        Ground,
        Escape,
        ControlSequence,
        String,
    }

    static FrameReason? Classify(List<byte> sequence, char final)
    {
        if (final is not ('h' or 'l'))
            return null;

        var body = new string(sequence.Select(b => (char)b).ToArray());
        if (!body.StartsWith('?'))
            return null;

        foreach (var parameter in body[1..].Split(';'))
        {
            switch (parameter)
            {
                case "2026" when final == 'l':
                    return FrameReason.SyncFrameClose;
                case "1049":
                    return final == 'h' ? FrameReason.AltScreenEnter : FrameReason.AltScreenLeave;
            }
        }

        return null;
    }
}
