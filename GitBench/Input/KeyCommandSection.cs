namespace GitBench.Input;

/// <summary>
/// The surface a <see cref="KeyCommand"/> belongs to, for grouping commands wherever the whole
/// table is shown. Declared in the order the groups are listed.
/// </summary>
public enum KeyCommandSection
{
    Application,
    Lists,
    Diff,
    Commits,
    Review,
    Walkthrough,
    CodeNavigation,
    Editor,
    Terminal,
}

public static class KeyCommandSections
{
    /// <summary>Every section, in display order.</summary>
    public static readonly IReadOnlyList<KeyCommandSection> All = Enum.GetValues<KeyCommandSection>();

    /// <summary>The commands in a section, in the order they are declared.</summary>
    public static IReadOnlyList<KeyCommand> CommandsIn(KeyCommandSection section)
    {
        var commands = new List<KeyCommand>();
        foreach (var command in Enum.GetValues<KeyCommand>())
            if (Of(command) == section)
                commands.Add(command);
        return commands;
    }

    public static KeyCommandSection Of(KeyCommand command) => command switch
    {
        KeyCommand.Refresh
            or KeyCommand.ToggleRepoBar
            or KeyCommand.ToggleAssistant
            or KeyCommand.FindInFile
            or KeyCommand.FindFile
            or KeyCommand.RepoHotkey1
            or KeyCommand.RepoHotkey2
            or KeyCommand.RepoHotkey3
            or KeyCommand.RepoHotkey4
            or KeyCommand.RepoHotkey5
            or KeyCommand.RepoHotkey6
            or KeyCommand.RepoHotkey7
            or KeyCommand.RepoHotkey8
            or KeyCommand.RepoHotkey9 => KeyCommandSection.Application,

        KeyCommand.ListUp
            or KeyCommand.ListDown
            or KeyCommand.ListCollapse
            or KeyCommand.ListExpand
            or KeyCommand.ListActivate
            or KeyCommand.ListDelete
            or KeyCommand.ListViewInDiff => KeyCommandSection.Lists,

        KeyCommand.ToggleFullFile => KeyCommandSection.Diff,

        KeyCommand.CommitCreateBranch
            or KeyCommand.CommitCreateTag
            or KeyCommand.CommitCherryPick
            or KeyCommand.CommitRevert => KeyCommandSection.Commits,

        KeyCommand.ReviewNextFile
            or KeyCommand.ReviewPrevFile
            or KeyCommand.ReviewToggleMark
            or KeyCommand.ReviewToggleHelp => KeyCommandSection.Review,

        KeyCommand.WalkthroughNext
            or KeyCommand.WalkthroughBack
            or KeyCommand.WalkthroughAsk => KeyCommandSection.Walkthrough,

        KeyCommand.GoToDefinition
            or KeyCommand.FindUsages
            or KeyCommand.NavigateBack
            or KeyCommand.NavigateForward => KeyCommandSection.CodeNavigation,

        KeyCommand.SaveFile
            or KeyCommand.ToggleLineComment
            or KeyCommand.ShowCompletions
            or KeyCommand.EditorZoomIn
            or KeyCommand.EditorZoomOut => KeyCommandSection.Editor,

        KeyCommand.TerminalCopy
            or KeyCommand.TerminalPaste
            or KeyCommand.TerminalSelectAll
            or KeyCommand.TerminalPageUp
            or KeyCommand.TerminalPageDown => KeyCommandSection.Terminal,

        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "No section."),
    };
}
