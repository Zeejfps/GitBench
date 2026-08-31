namespace GitBench.Terminal.Vt;

/// <summary>Which of the pane's own colours a dynamic-colour sequence addressed.</summary>
public enum TerminalColorSlot : byte
{
    /// <summary>What <see cref="TerminalColorKind.Default"/> foreground resolves to, <c>OSC 10</c>.</summary>
    Foreground = 0,

    /// <summary>What <see cref="TerminalColorKind.Default"/> background resolves to, <c>OSC 11</c>.</summary>
    Background = 1,

    /// <summary>The colour the caret is drawn in, <c>OSC 12</c>.</summary>
    Cursor = 2,
}

/// <summary>
/// What the pane looks like, for the programs that ask.
/// </summary>
/// <remarks>
/// <para>
/// Asked rather than stored, and asked at the moment a sequence arrives. A cell's colour is a
/// <see cref="TerminalColor"/> — three cases, no pixels — precisely because only the renderer can
/// say what "default" is, and that answer follows the user's theme. Handing the engine a copy of
/// those colours would make it the second place that believes it knows, and the two would disagree
/// the first time the theme changed.
/// </para>
/// <para>
/// This is a read. A program cannot set these: the sequence that would is parsed and ignored,
/// because the surface belongs to the application's theme rather than to whatever last ran in the
/// pane, and a program that died mid-session would otherwise leave the pane a colour the user's
/// own light/dark toggle no longer controls.
/// </para>
/// </remarks>
public interface ITerminalPalette
{
    /// <summary>The colour of one slot, as the renderer would currently draw it.</summary>
    TerminalRgb Resolve(TerminalColorSlot slot);
}

/// <summary>An opaque 24-bit colour, in the form the dynamic-colour reply reports.</summary>
public readonly record struct TerminalRgb(byte Red, byte Green, byte Blue)
{
    /// <summary>
    /// The <c>rgb:</c> form of <c>XParseColor</c>, which is what xterm answers a query with.
    /// </summary>
    /// <remarks>
    /// Sixteen bits per channel, each eight-bit value doubled rather than shifted, so that a full
    /// channel reports as <c>ffff</c> and not <c>ff00</c> — the scaling every reference terminal
    /// uses, and the one that survives a client dividing back down to eight bits.
    /// </remarks>
    public string ToXParseColor() => $"rgb:{Red:x2}{Red:x2}/{Green:x2}{Green:x2}/{Blue:x2}{Blue:x2}";
}
