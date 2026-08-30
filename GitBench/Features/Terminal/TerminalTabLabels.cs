namespace GitBench.Features.Terminal;

/// <summary>
/// What one tab says: the terminal's own name, and the position it holds among the tabs that would
/// otherwise say the same thing.
/// </summary>
/// <remarks>
/// The index is null unless there is something to tell apart, so the ordinary case reads as a plain
/// name. It is positional and belongs to the strip rather than to the terminal: it means "the second
/// one you can see" and stops meaning anything once the tab beside it closes.
/// </remarks>
internal readonly record struct TerminalTabLabel(string Text, int? Index);

/// <summary>Names the tabs in a strip. Pure, so what a strip of four shells reads as is testable.</summary>
internal static class TerminalTabLabels
{
    /// <summary>
    /// What a terminal is called: the name the user gave it, else the title a program running in it
    /// set, else the shell's own name.
    /// </summary>
    /// <remarks>
    /// An idle tab has no shell and therefore no title; an exited one keeps whatever it last said,
    /// which is what a reader looking for the command that finished expects to still see. A name the
    /// user typed outranks both: a tab is renamed precisely so it stops following whatever is
    /// running in it.
    /// </remarks>
    public static string NameOf(TerminalInstance terminal) =>
        terminal.GivenName.Value ?? terminal.Title.Value ?? terminal.Name;

    /// <summary>The label for one terminal, read against the strip it sits in.</summary>
    public static TerminalTabLabel For(IReadOnlyList<TerminalInstance> terminals, TerminalInstance terminal)
    {
        var name = NameOf(terminal);

        var sharing = 0;
        var position = 0;
        foreach (var other in terminals)
        {
            if (NameOf(other) != name) continue;
            sharing++;
            if (ReferenceEquals(other, terminal)) position = sharing;
        }

        return new TerminalTabLabel(name, sharing > 1 ? position : null);
    }
}
