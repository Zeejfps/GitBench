namespace GitBench.Input;

/// <summary>
/// Every action a keyboard shortcut can run. Handlers ask the <see cref="IKeyMap"/> whether a key
/// press means one of these rather than comparing against a key, and menus and cheatsheets ask it
/// what the shortcut is called — so the binding lives in exactly one table.
/// </summary>
public enum KeyCommand
{
    // App-wide, dispatched from the root of the main window.
    Refresh,
    ToggleRepoBar,
    ToggleAssistant,
    FindInFile,
    FindFile,
    RepoHotkey1,
    RepoHotkey2,
    RepoHotkey3,
    RepoHotkey4,
    RepoHotkey5,
    RepoHotkey6,
    RepoHotkey7,
    RepoHotkey8,
    RepoHotkey9,

    // A focused row list.
    ListUp,
    ListDown,
    ListCollapse,
    ListExpand,
    ListActivate,
    ListDelete,
    ListViewInDiff,

    // A diff surface, whether beside a file list or in its own window.
    ToggleFullFile,

    // The selected commit in the history list.
    CommitCreateBranch,
    CommitCreateTag,
    CommitCherryPick,
    CommitRevert,

    // The review loop, over a review window or the working-tree review.
    ReviewNextFile,
    ReviewPrevFile,
    ReviewToggleMark,
    ReviewToggleHelp,

    // The walkthrough rail, while a narrator has steps up in a review window.
    WalkthroughNext,
    WalkthroughBack,
    WalkthroughAsk,

    // Code navigation over a source view.
    GoToDefinition,
    FindUsages,
    NavigateBack,
    NavigateForward,

    // An editable file.
    SaveFile,
    ToggleLineComment,

    // Any file or diff body, editable or not: the code's own size, apart from the UI.
    EditorZoomIn,
    EditorZoomOut,

    // The terminal pane's own chords.
    TerminalCopy,
    TerminalPaste,
    TerminalSelectAll,
    TerminalPageUp,
    TerminalPageDown,
}

public static class KeyCommands
{
    /// <summary>The repo hotkey commands in slot order, so slot N is at index N-1.</summary>
    public static readonly IReadOnlyList<KeyCommand> RepoHotkeys =
    [
        KeyCommand.RepoHotkey1,
        KeyCommand.RepoHotkey2,
        KeyCommand.RepoHotkey3,
        KeyCommand.RepoHotkey4,
        KeyCommand.RepoHotkey5,
        KeyCommand.RepoHotkey6,
        KeyCommand.RepoHotkey7,
        KeyCommand.RepoHotkey8,
        KeyCommand.RepoHotkey9,
    ];

    /// <summary>The 1-based slot a repo hotkey command stands for, or null for any other command.</summary>
    public static int? RepoHotkeySlot(KeyCommand command)
    {
        var index = (int)command - (int)KeyCommand.RepoHotkey1;
        return index is >= 0 and < 9 ? index + 1 : null;
    }
}
