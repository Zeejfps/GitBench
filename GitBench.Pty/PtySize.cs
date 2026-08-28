namespace GitBench.Pty;

/// <summary>
/// The character dimensions of a pseudo-terminal.
/// </summary>
public readonly record struct PtySize
{
    public PtySize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, ushort.MaxValue);

        Columns = columns;
        Rows = rows;
    }

    public int Columns { get; }

    public int Rows { get; }

    public static PtySize Default => new(80, 24);
}
