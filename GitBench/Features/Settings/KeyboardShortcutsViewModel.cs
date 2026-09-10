using GitBench.Input;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Features.Settings;

/// <summary>One command as the shortcuts list shows it: its name and every distinct cap that runs it.</summary>
internal sealed record ShortcutRow(KeyCommand Command, string Label, IReadOnlyList<string> Caps);

/// <summary>A titled group of shortcut rows.</summary>
internal sealed record ShortcutSection(KeyCommandSection Section, string Title, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// The whole key map as a list to read: every command, grouped by the surface it belongs to, with
/// the caps the active <see cref="IKeyMap"/> binds it to. Follows the locale, so the names re-read
/// in the new language.
/// </summary>
internal sealed class KeyboardShortcutsViewModel
{
    public KeyboardShortcutsViewModel(IKeyMap keys, ILocalizationService localization)
    {
        Sections = new Derived<IReadOnlyList<ShortcutSection>>(() => Build(keys, localization.Strings.Value));
    }

    public IReadable<IReadOnlyList<ShortcutSection>> Sections { get; }

    private static IReadOnlyList<ShortcutSection> Build(IKeyMap keys, Strings s)
    {
        var sections = new List<ShortcutSection>(KeyCommandSections.All.Count);
        foreach (var section in KeyCommandSections.All)
        {
            var commands = KeyCommandSections.CommandsIn(section);
            var rows = new List<ShortcutRow>(commands.Count);
            foreach (var command in commands)
                rows.Add(new ShortcutRow(command, Label(s, command), Caps(keys, command)));
            sections.Add(new ShortcutSection(section, SectionTitle(s, section), rows));
        }

        return sections;
    }

    // Enter and its numpad twin read the same, and one cap says it.
    private static IReadOnlyList<string> Caps(IKeyMap keys, KeyCommand command)
    {
        var caps = new List<string>();
        foreach (var gesture in keys.GesturesFor(command))
            if (!caps.Contains(gesture.Display))
                caps.Add(gesture.Display);
        return caps;
    }

    public static string SectionTitle(Strings s, KeyCommandSection section) => section switch
    {
        KeyCommandSection.Application => s.ShortcutsSectionApplication,
        KeyCommandSection.Lists => s.ShortcutsSectionLists,
        KeyCommandSection.Diff => s.ShortcutsSectionDiff,
        KeyCommandSection.Commits => s.ShortcutsSectionCommits,
        KeyCommandSection.Review => s.ShortcutsSectionReview,
        KeyCommandSection.CodeNavigation => s.ShortcutsSectionCodeNavigation,
        KeyCommandSection.Editor => s.ShortcutsSectionEditor,
        KeyCommandSection.Terminal => s.ShortcutsSectionTerminal,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "No title."),
    };

    public static string Label(Strings s, KeyCommand command)
    {
        if (KeyCommands.RepoHotkeySlot(command) is { } slot) return s.ShortcutsCommandRepoHotkey(slot);
        return LabelOfSingle(s, command);
    }

    private static string LabelOfSingle(Strings s, KeyCommand command) => command switch
    {
        KeyCommand.Refresh => s.ShortcutsCommandRefresh,
        KeyCommand.ToggleRepoBar => s.ShortcutsCommandToggleRepoBar,
        KeyCommand.ToggleAssistant => s.ShortcutsCommandToggleAssistant,
        KeyCommand.FindInFile => s.ShortcutsCommandFindInFile,
        KeyCommand.FindFile => s.ShortcutsCommandFindFile,

        KeyCommand.ListUp => s.ShortcutsCommandListUp,
        KeyCommand.ListDown => s.ShortcutsCommandListDown,
        KeyCommand.ListCollapse => s.ShortcutsCommandListCollapse,
        KeyCommand.ListExpand => s.ShortcutsCommandListExpand,
        KeyCommand.ListActivate => s.ShortcutsCommandListActivate,
        KeyCommand.ListDelete => s.ShortcutsCommandListDelete,
        KeyCommand.ListViewInDiff => s.ShortcutsCommandListViewInDiff,

        KeyCommand.ToggleFullFile => s.ShortcutsCommandToggleFullFile,

        KeyCommand.CommitCreateBranch => s.ShortcutsCommandCommitCreateBranch,
        KeyCommand.CommitCreateTag => s.ShortcutsCommandCommitCreateTag,
        KeyCommand.CommitCherryPick => s.ShortcutsCommandCommitCherryPick,
        KeyCommand.CommitRevert => s.ShortcutsCommandCommitRevert,

        KeyCommand.ReviewNextFile => s.ShortcutsCommandReviewNextFile,
        KeyCommand.ReviewPrevFile => s.ShortcutsCommandReviewPrevFile,
        KeyCommand.ReviewToggleMark => s.ShortcutsCommandReviewToggleMark,
        KeyCommand.ReviewToggleHelp => s.ShortcutsCommandReviewToggleHelp,

        KeyCommand.GoToDefinition => s.ShortcutsCommandGoToDefinition,
        KeyCommand.FindUsages => s.ShortcutsCommandFindUsages,
        KeyCommand.NavigateBack => s.ShortcutsCommandNavigateBack,
        KeyCommand.NavigateForward => s.ShortcutsCommandNavigateForward,

        KeyCommand.SaveFile => s.ShortcutsCommandSaveFile,
        KeyCommand.ToggleLineComment => s.ShortcutsCommandToggleLineComment,

        KeyCommand.TerminalCopy => s.ShortcutsCommandTerminalCopy,
        KeyCommand.TerminalPaste => s.ShortcutsCommandTerminalPaste,
        KeyCommand.TerminalSelectAll => s.ShortcutsCommandTerminalSelectAll,
        KeyCommand.TerminalPageUp => s.ShortcutsCommandTerminalPageUp,
        KeyCommand.TerminalPageDown => s.ShortcutsCommandTerminalPageDown,

        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "No label."),
    };
}
