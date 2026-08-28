namespace GitBench.Terminal.Vt;

/// <summary>
/// The character dimensions of a terminal viewport.
/// </summary>
public readonly record struct TerminalSize
{
    public TerminalSize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        Columns = columns;
        Rows = rows;
    }

    public int Columns { get; }

    public int Rows { get; }

    public override string ToString() => $"{Columns}x{Rows}";
}
