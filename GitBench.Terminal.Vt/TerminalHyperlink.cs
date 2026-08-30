namespace GitBench.Terminal.Vt;

/// <summary>
/// Which hyperlink a cell belongs to, as an opaque token issued by one grid.
/// </summary>
/// <remarks>
/// <para>
/// A token rather than the url itself because a cell is copied by value for every row of every
/// frame, and because it is what makes a link's extent answerable: two cells are the same link when
/// their ids are equal, whatever sits between them and whichever row each ended up on after a
/// reflow. Resolving one to a url is <see cref="ITerminalGrid.TryGetHyperlink"/>.
/// </para>
/// <para>
/// Ids are never reused within a grid, so an id whose link has gone resolves to nothing — never to
/// a different url. Comparing ids issued by <em>different</em> grids is meaningless, and nothing
/// here can stop it; the ids a pane compares all come from the one grid it draws.
/// </para>
/// </remarks>
public readonly record struct HyperlinkId(int Value)
{
    /// <summary>The cell is ordinary text and belongs to no link.</summary>
    public static HyperlinkId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => IsNone ? "none" : $"link {Value}";
}

/// <summary>
/// Where a hyperlink points, exactly as the program wrote it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately unvalidated, and deliberately not a <see cref="Uri"/>. A terminal's job is to report
/// that a program marked these cells as a link to this text; whether the text is a url anyone should
/// follow is a policy question with a different answer for every host, and one an engine has no
/// standing to answer. A <c>file:</c> link resolves here and is a link — it is simply not one this
/// application will open, which is decided where the opening happens.
/// </para>
/// <para>
/// The one guarantee is that it is non-empty and no longer than the engine's cap, because a url is
/// interned before the cells naming it are printed and there is no later moment to reject it in.
/// </para>
/// </remarks>
public sealed record TerminalHyperlink(string Uri)
{
    public override string ToString() => Uri;
}
