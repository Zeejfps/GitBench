using System.Text;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// Escape-sequence literals, spelled the way the recorded inventories spell them, so a test reads
/// as the bytes a program actually emits rather than as a wall of unicode escapes.
/// </summary>
public static class Vt
{
    public const string Esc = "";
    public const string Csi = "[";
    public const string Osc = "]";
    public const string St = "\\";
    public const string Bel = "";

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}

/// <summary>
/// Reading an engine the way an assertion wants to. Every helper goes through
/// <see cref="ITerminalGrid"/> and <see cref="TerminalState"/>, so nothing here can reach past the
/// seam into a particular engine.
/// </summary>
public static class EngineExtensions
{
    /// <summary>Feeds text as UTF-8, the way a program's output arrives.</summary>
    public static FeedResult Feed(this ITerminalEngine engine, string text) =>
        engine.Feed(Vt.Bytes(text));

    /// <summary>Feeds one byte at a time, the worst case a pseudo-terminal read can hand over.</summary>
    public static void FeedByteAtATime(this ITerminalEngine engine, string text)
    {
        Span<byte> one = stackalloc byte[1];
        foreach (var b in Vt.Bytes(text))
        {
            one[0] = b;
            engine.Feed(one);
        }
    }

    public static TerminalCell CellAt(this ITerminalEngine engine, int column, int row) =>
        engine.Grid.Cell(column, row);

    public static string RowText(this ITerminalEngine engine, int row) => engine.Grid.RowText(row);

    /// <summary>The cursor cell, as a tuple, so a wrong position reads as two numbers in a diff.</summary>
    public static (int Column, int Row) CursorAt(this ITerminalEngine engine) =>
        (engine.State.Cursor.Column, engine.State.Cursor.Row);

    public static string Text(this FeedResult result) => Encoding.UTF8.GetString(result.Response.Span);

    /// <summary>The response with ESC rendered as <c>^[</c>, so a failure message is readable.</summary>
    public static string Printable(this FeedResult result) =>
        result.Text().Replace(Vt.Esc, "^[", StringComparison.Ordinal);
}
