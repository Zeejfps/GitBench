namespace GitBench.Terminal.Vt;

/// <summary>
/// A range of viewport rows, inclusive at both ends.
/// </summary>
public readonly record struct RowSpan(int First, int Last)
{
    public static RowSpan None { get; } = new(int.MaxValue, int.MinValue);

    public bool IsEmpty => Last < First;

    /// <summary>True when the span covers every row of a viewport that tall.</summary>
    public bool Covers(int rows) => !IsEmpty && First <= 0 && Last >= rows - 1;

    public override string ToString() => IsEmpty ? "none" : $"rows {First}..{Last}";
}

/// <summary>
/// What one <see cref="ITerminalEngine.Feed"/> produced besides the mutated grid.
/// </summary>
/// <remarks>
/// <para>
/// Returned rather than raised as events. A read pump needs to know whether to repaint and whether
/// to write back, and both answers belong to the call that produced them; it also means a test can
/// observe a device-status reply without standing up a fake delegate to catch it.
/// </para>
/// <para>
/// <see cref="LinesScrolled"/> counts the lines that left the top of the screen for the scrollback,
/// which is not the same as the growth of <see cref="ITerminalGrid.ScrollbackRows"/>: once the
/// history is at its configured depth it stops growing and the oldest line is dropped instead, and
/// a reader scrolled up needs to know the content moved under it either way.
/// </para>
/// </remarks>
public readonly record struct FeedResult(
    RowSpan Damage,
    ReadOnlyMemory<byte> Response,
    int FramesCompleted,
    bool FramePending,
    int LinesScrolled)
{
    public static FeedResult Nothing { get; } =
        new(RowSpan.None, ReadOnlyMemory<byte>.Empty, 0, false, 0);

    public bool HasResponse => !Response.IsEmpty;
}
