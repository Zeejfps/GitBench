using GitBench.Terminal.Vt;

namespace GitBench.Features.Terminal;

/// <summary>
/// How a run of terminal cells is drawn: colours already resolved to pixels, plus the attributes
/// that survive as a choice of face and decoration.
/// </summary>
/// <remarks>
/// The renderer's whole vocabulary, and deliberately smaller than <see cref="CellAttributes"/>. The
/// attributes that change a cell's colour rather than its form — inverse, dim, hidden — are already
/// spent by the time a cell becomes a <see cref="RunStyle"/>, so a renderer cannot forget to apply
/// one; what is left is exactly what a draw call still has to be told. Blink has no representation
/// because nothing drives one yet.
/// </remarks>
internal readonly record struct RunStyle(
    uint Foreground,
    uint Background,
    bool Bold,
    bool Italic,
    bool Underline,
    bool StrikeThrough);

/// <summary>
/// Turns one cell into the way it is drawn.
/// </summary>
/// <remarks>
/// The seam between resolving colour and splitting a row into runs: the splitter needs to know when
/// two cells look the same, and nothing more about how either was decided. It also lets a run test
/// state its styles outright instead of standing up a theme to produce them.
/// </remarks>
internal interface ICellStyler
{
    RunStyle Style(in TerminalCell cell);
}
