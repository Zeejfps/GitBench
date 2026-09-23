using GitBench.Input;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Features.Settings;

/// <summary>
/// One command as the shortcuts list shows it: its name, every distinct cap that runs it, whether
/// those are still the built-in ones, and the names of any commands the same keys also fire.
/// </summary>
internal sealed record ShortcutRow(
    KeyCommand Command,
    string Label,
    IReadOnlyList<string> Caps,
    bool IsDefault,
    IReadOnlyList<string> ConflictsWith);

/// <summary>A titled group of shortcut rows.</summary>
internal sealed record ShortcutSection(KeyCommandSection Section, string Title, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// The whole key map as a list to read and edit: every command, grouped by the surface it belongs
/// to, with the caps the <see cref="KeyMap"/> binds it to. Follows the locale, so the
/// names re-read in the new language; narrows to what <see cref="Query"/> matches — a command's
/// name, one of its caps, or the group it sits in; and re-reads after every rebind. One command at
/// a time can be <see cref="Recording"/> a new gesture.
/// </summary>
internal sealed class KeyboardShortcutsViewModel
{
    private readonly KeyMap _keys;

    public KeyboardShortcutsViewModel(KeyMap keys, ILocalizationService localization)
    {
        _keys = keys;
        Sections = new Derived<IReadOnlyList<ShortcutSection>>(() =>
        {
            _ = keys.Version.Value;
            return Filter(Build(keys, localization.Strings.Value), Query.Value.Trim());
        });
        NoMatches = new Derived<bool>(() => Sections.Value.Count == 0);
        HasOverrides = new Derived<bool>(() =>
        {
            _ = keys.Version.Value;
            return keys.Overrides.Count > 0;
        });
    }

    /// <summary>What the search box holds. Empty shows everything.</summary>
    public State<string> Query { get; } = new(string.Empty);

    /// <summary>The groups with at least one row matching <see cref="Query"/>, in display order.</summary>
    public IReadable<IReadOnlyList<ShortcutSection>> Sections { get; }

    /// <summary>True when a query rules every command out.</summary>
    public IReadable<bool> NoMatches { get; }

    /// <summary>True while any command is off its built-in gestures.</summary>
    public IReadable<bool> HasOverrides { get; }

    /// <summary>The command waiting for its next gesture, or null while none is.</summary>
    public State<KeyCommand?> Recording { get; } = new(null);

    public void BeginRecording(KeyCommand command) => Recording.Value = command;

    public void CancelRecording() => Recording.Value = null;

    /// <summary>Binds the recording command to the gesture and ends the recording; a no-op with none recording.</summary>
    public void CommitRecording(KeyGesture gesture)
    {
        if (Recording.Value is not { } command) return;
        Recording.Value = null;
        _keys.Rebind(command, gesture);
    }

    public void Reset(KeyCommand command) => _keys.Reset(command);

    public void ResetAll() => _keys.ResetAll();

    private static IReadOnlyList<ShortcutSection> Build(KeyMap keys, Strings s)
    {
        var sections = new List<ShortcutSection>(KeyCommandSections.All.Count);
        foreach (var section in KeyCommandSections.All)
        {
            var commands = KeyCommandSections.CommandsIn(section);
            var rows = new List<ShortcutRow>(commands.Count);
            foreach (var command in commands)
            {
                rows.Add(new ShortcutRow(
                    command,
                    Label(s, command),
                    Caps(keys, command),
                    keys.IsDefault(command),
                    Conflicts(keys, s, command)));
            }

            sections.Add(new ShortcutSection(section, SectionTitle(s, section), rows));
        }

        return sections;
    }

    private static IReadOnlyList<ShortcutSection> Filter(IReadOnlyList<ShortcutSection> all, string query)
    {
        if (query.Length == 0) return all;

        var kept = new List<ShortcutSection>(all.Count);
        foreach (var section in all)
        {
            if (Contains(section.Title, query))
            {
                kept.Add(section);
                continue;
            }

            var rows = new List<ShortcutRow>();
            foreach (var row in section.Rows)
                if (Matches(row, query))
                    rows.Add(row);
            if (rows.Count > 0)
                kept.Add(section with { Rows = rows });
        }

        return kept;
    }

    private static bool Matches(ShortcutRow row, string query)
    {
        if (Contains(row.Label, query)) return true;
        foreach (var cap in row.Caps)
            if (Contains(cap, query))
                return true;
        return false;
    }

    private static bool Contains(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

    // Enter and its numpad twin read the same, and one cap says it.
    private static IReadOnlyList<string> Caps(IKeyMap keys, KeyCommand command)
    {
        var caps = new List<string>();
        foreach (var gesture in keys.GesturesFor(command))
            if (!caps.Contains(gesture.Display))
                caps.Add(gesture.Display);
        return caps;
    }

    private static IReadOnlyList<string> Conflicts(KeyMap keys, Strings s, KeyCommand command)
    {
        var labels = new List<string>();
        foreach (var gesture in keys.GesturesFor(command))
        {
            foreach (var other in keys.ConflictsWith(command, gesture))
            {
                var label = Label(s, other);
                if (!labels.Contains(label))
                    labels.Add(label);
            }
        }

        return labels;
    }

    public static string SectionTitle(Strings s, KeyCommandSection section) => section switch
    {
        KeyCommandSection.Application => s.ShortcutsSectionApplication,
        KeyCommandSection.Lists => s.ShortcutsSectionLists,
        KeyCommandSection.Diff => s.ShortcutsSectionDiff,
        KeyCommandSection.Commits => s.ShortcutsSectionCommits,
        KeyCommandSection.Review => s.ShortcutsSectionReview,
        KeyCommandSection.Walkthrough => s.ShortcutsSectionWalkthrough,
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

        KeyCommand.WalkthroughNext => s.ShortcutsCommandWalkthroughNext,
        KeyCommand.WalkthroughBack => s.ShortcutsCommandWalkthroughBack,
        KeyCommand.WalkthroughAsk => s.ShortcutsCommandWalkthroughAsk,

        KeyCommand.GoToDefinition => s.ShortcutsCommandGoToDefinition,
        KeyCommand.FindUsages => s.ShortcutsCommandFindUsages,
        KeyCommand.NavigateBack => s.ShortcutsCommandNavigateBack,
        KeyCommand.NavigateForward => s.ShortcutsCommandNavigateForward,

        KeyCommand.SaveFile => s.ShortcutsCommandSaveFile,
        KeyCommand.ToggleLineComment => s.ShortcutsCommandToggleLineComment,
        KeyCommand.ShowCompletions => s.ShortcutsCommandShowCompletions,
        KeyCommand.ParameterInfo => s.ShortcutsCommandParameterInfo,
        KeyCommand.EditorZoomIn => s.ShortcutsCommandEditorZoomIn,
        KeyCommand.EditorZoomOut => s.ShortcutsCommandEditorZoomOut,

        KeyCommand.TerminalCopy => s.ShortcutsCommandTerminalCopy,
        KeyCommand.TerminalPaste => s.ShortcutsCommandTerminalPaste,
        KeyCommand.TerminalSelectAll => s.ShortcutsCommandTerminalSelectAll,
        KeyCommand.TerminalPageUp => s.ShortcutsCommandTerminalPageUp,
        KeyCommand.TerminalPageDown => s.ShortcutsCommandTerminalPageDown,

        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "No label."),
    };
}
