using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Input;

/// <summary>The default bindings for every <see cref="KeyCommand"/>.</summary>
public sealed class KeyMap : IKeyMap
{
    /// <summary>The shared default table.</summary>
    public static KeyMap Defaults { get; } = new();

    private readonly Dictionary<KeyCommand, KeyGesture[]> _bindings;

    public KeyMap()
    {
        _bindings = new Dictionary<KeyCommand, KeyGesture[]>();
        foreach (var command in Enum.GetValues<KeyCommand>())
            _bindings[command] = DefaultGestures(command);
    }

    public IReadOnlyList<KeyGesture> GesturesFor(KeyCommand command) => _bindings[command];

    public bool Matches(KeyCommand command, KeyboardKey key, InputModifiers modifiers)
    {
        foreach (var gesture in _bindings[command])
            if (gesture.Matches(key, modifiers))
                return true;
        return false;
    }

    public string Display(KeyCommand command) => _bindings[command][0].Display;

    private static KeyGesture[] DefaultGestures(KeyCommand command) => command switch
    {
        KeyCommand.Refresh => [new(KeyboardKey.F5)],
        KeyCommand.ToggleRepoBar => [KeyGesture.WithPrimary(KeyboardKey.B)],
        KeyCommand.ToggleAssistant => [KeyGesture.WithPrimary(KeyboardKey.K)],
        KeyCommand.FindInFile => [KeyGesture.WithPrimary(KeyboardKey.F)],
        KeyCommand.FindFile => [KeyGesture.WithPrimary(KeyboardKey.P)],
        KeyCommand.RepoHotkey1 => RepoHotkey(KeyboardKey.Alpha1, KeyboardKey.Numpad1),
        KeyCommand.RepoHotkey2 => RepoHotkey(KeyboardKey.Alpha2, KeyboardKey.Numpad2),
        KeyCommand.RepoHotkey3 => RepoHotkey(KeyboardKey.Alpha3, KeyboardKey.Numpad3),
        KeyCommand.RepoHotkey4 => RepoHotkey(KeyboardKey.Alpha4, KeyboardKey.Numpad4),
        KeyCommand.RepoHotkey5 => RepoHotkey(KeyboardKey.Alpha5, KeyboardKey.Numpad5),
        KeyCommand.RepoHotkey6 => RepoHotkey(KeyboardKey.Alpha6, KeyboardKey.Numpad6),
        KeyCommand.RepoHotkey7 => RepoHotkey(KeyboardKey.Alpha7, KeyboardKey.Numpad7),
        KeyCommand.RepoHotkey8 => RepoHotkey(KeyboardKey.Alpha8, KeyboardKey.Numpad8),
        KeyCommand.RepoHotkey9 => RepoHotkey(KeyboardKey.Alpha9, KeyboardKey.Numpad9),

        KeyCommand.ListUp => [new(KeyboardKey.UpArrow)],
        KeyCommand.ListDown => [new(KeyboardKey.DownArrow)],
        KeyCommand.ListCollapse => [new(KeyboardKey.LeftArrow)],
        KeyCommand.ListExpand => [new(KeyboardKey.RightArrow)],
        KeyCommand.ListActivate => [new(KeyboardKey.Enter), new(KeyboardKey.NumpadEnter)],
        KeyCommand.ListDelete => [new(KeyboardKey.Delete)],
        KeyCommand.ListViewInDiff => [new(KeyboardKey.Space)],

        KeyCommand.ToggleFullFile => [new(KeyboardKey.F)],

        KeyCommand.CommitCreateBranch => [new(KeyboardKey.B)],
        KeyCommand.CommitCreateTag => [new(KeyboardKey.T)],
        KeyCommand.CommitCherryPick => [new(KeyboardKey.C)],
        KeyCommand.CommitRevert => [new(KeyboardKey.V)],

        KeyCommand.ReviewNextFile => [new(KeyboardKey.J)],
        KeyCommand.ReviewPrevFile => [new(KeyboardKey.K)],
        KeyCommand.ReviewToggleMark =>
            [new(KeyboardKey.V), new(KeyboardKey.Space), new(KeyboardKey.Enter), new(KeyboardKey.NumpadEnter)],
        KeyCommand.ReviewToggleHelp => [new(KeyboardKey.Slash, InputModifiers.Shift)],

        KeyCommand.GoToDefinition => [new(KeyboardKey.F12)],
        KeyCommand.FindUsages => [new(KeyboardKey.F12, InputModifiers.Shift)],
        KeyCommand.NavigateBack => [KeyGesture.WithPrimary(KeyboardKey.LeftBracket)],
        KeyCommand.NavigateForward => [KeyGesture.WithPrimary(KeyboardKey.RightBracket)],

        KeyCommand.SaveFile => [KeyGesture.WithPrimary(KeyboardKey.S)],
        KeyCommand.ToggleLineComment => [KeyGesture.WithPrimary(KeyboardKey.Slash)],

        // Cmd on macOS, Ctrl+Shift elsewhere: Ctrl+C is the shell's interrupt, and Shift is already
        // the modifier the pane takes back from the shell for the wheel and the page keys.
        KeyCommand.TerminalCopy => [TerminalChord(KeyboardKey.C)],
        KeyCommand.TerminalPaste => [TerminalChord(KeyboardKey.V)],
        KeyCommand.TerminalSelectAll => [TerminalChord(KeyboardKey.A)],
        KeyCommand.TerminalPageUp => [new(KeyboardKey.PageUp, InputModifiers.Shift)],
        KeyCommand.TerminalPageDown => [new(KeyboardKey.PageDown, InputModifiers.Shift)],

        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "No default binding."),
    };

    private static KeyGesture[] RepoHotkey(KeyboardKey digit, KeyboardKey numpad) =>
        [KeyGesture.WithPrimary(digit), KeyGesture.WithPrimary(numpad)];

    private static KeyGesture TerminalChord(KeyboardKey key) => OperatingSystem.IsMacOS()
        ? new KeyGesture(key, InputModifiers.Super)
        : new KeyGesture(key, InputModifiers.Control | InputModifiers.Shift);
}
