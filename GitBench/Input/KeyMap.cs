using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Input;

/// <summary>
/// The bindings for every <see cref="KeyCommand"/>: the built-in table, with whatever the user has
/// rebound laid over it.
/// </summary>
public sealed class KeyMap : IKeyMap
{
    /// <summary>The built-in table, shared and never edited: it carries no editing surface at all.</summary>
    public static IKeyMap Defaults { get; } = new ReadOnly(new KeyMap());

    private readonly Dictionary<KeyCommand, KeyGesture[]> _bindings = new();
    private readonly State<int> _version = new(0);

    public KeyMap() : this([]) { }

    /// <summary>Starts from the built-in table with these overrides applied; an override with no
    /// gestures leaves its command on the defaults.</summary>
    public KeyMap(IEnumerable<KeyBinding> overrides)
    {
        foreach (var command in Enum.GetValues<KeyCommand>())
            _bindings[command] = DefaultGestures(command);
        foreach (var binding in overrides)
            if (binding.Gestures.Count > 0)
                _bindings[binding.Command] = binding.Gestures.ToArray();
    }

    public IReadable<int> Version => _version;

    public IReadOnlyList<KeyGesture> GesturesFor(KeyCommand command) => _bindings[command];

    public bool Matches(KeyCommand command, KeyboardKey key, InputModifiers modifiers)
    {
        foreach (var gesture in _bindings[command])
            if (gesture.Matches(key, modifiers))
                return true;
        return false;
    }

    public string Display(KeyCommand command) => _bindings[command][0].Display;

    public IReadOnlyList<KeyGesture> DefaultsFor(KeyCommand command) => DefaultGestures(command);

    public bool IsDefault(KeyCommand command) => _bindings[command].AsSpan().SequenceEqual(DefaultGestures(command));

    public void Rebind(KeyCommand command, KeyGesture gesture)
    {
        if (_bindings[command] is [var only] && only == gesture) return;
        _bindings[command] = [gesture];
        _version.Value++;
    }

    public void Reset(KeyCommand command)
    {
        if (IsDefault(command)) return;
        _bindings[command] = DefaultGestures(command);
        _version.Value++;
    }

    public void ResetAll()
    {
        var changed = false;
        foreach (var command in Enum.GetValues<KeyCommand>())
        {
            if (IsDefault(command)) continue;
            _bindings[command] = DefaultGestures(command);
            changed = true;
        }

        if (changed) _version.Value++;
    }

    public IReadOnlyList<KeyBinding> Overrides
    {
        get
        {
            var overrides = new List<KeyBinding>();
            foreach (var command in Enum.GetValues<KeyCommand>())
                if (!IsDefault(command))
                    overrides.Add(new KeyBinding(command, _bindings[command]));
            return overrides;
        }
    }

    public IReadOnlyList<KeyCommand> ConflictsWith(KeyCommand command, KeyGesture gesture)
    {
        var section = KeyCommandSections.Of(command);
        var conflicts = new List<KeyCommand>();
        foreach (var other in Enum.GetValues<KeyCommand>())
        {
            if (other == command) continue;
            var otherSection = KeyCommandSections.Of(other);
            var sharesPath = otherSection == section
                || otherSection == KeyCommandSection.Application
                || section == KeyCommandSection.Application;
            if (!sharesPath) continue;
            foreach (var bound in _bindings[other])
            {
                if (bound != gesture) continue;
                conflicts.Add(other);
                break;
            }
        }

        return conflicts;
    }

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

        KeyCommand.WalkthroughNext => [new(KeyboardKey.Space), new(KeyboardKey.N)],
        KeyCommand.WalkthroughBack => [new(KeyboardKey.P)],
        KeyCommand.WalkthroughAsk => [new(KeyboardKey.Slash)],

        KeyCommand.GoToDefinition => [new(KeyboardKey.F12)],
        KeyCommand.FindUsages => [new(KeyboardKey.F12, InputModifiers.Shift)],
        KeyCommand.NavigateBack => [KeyGesture.WithPrimary(KeyboardKey.LeftBracket)],
        KeyCommand.NavigateForward => [KeyGesture.WithPrimary(KeyboardKey.RightBracket)],

        KeyCommand.SaveFile => [KeyGesture.WithPrimary(KeyboardKey.S)],
        KeyCommand.ToggleLineComment => [KeyGesture.WithPrimary(KeyboardKey.Slash)],
        // Ctrl on macOS too: Cmd+Space is Spotlight.
        KeyCommand.ShowCompletions => [new(KeyboardKey.Space, InputModifiers.Control)],
        // Not Rider's Ctrl+P, which is already the app's Find File.
        KeyCommand.ParameterInfo => [new(KeyboardKey.Space, InputModifiers.Control | InputModifiers.Shift)],
        KeyCommand.EditorZoomIn =>
        [
            KeyGesture.WithPrimary(KeyboardKey.Equals),
            KeyGesture.WithPrimary(KeyboardKey.Equals, InputModifiers.Shift),
            KeyGesture.WithPrimary(KeyboardKey.NumpadAdd),
        ],
        KeyCommand.EditorZoomOut =>
            [KeyGesture.WithPrimary(KeyboardKey.Minus), KeyGesture.WithPrimary(KeyboardKey.NumpadSubtract)],

        // Cmd on macOS, Ctrl+Shift elsewhere: Ctrl+C is the shell's interrupt, and Shift is already
        // the modifier the pane takes back from the shell for the wheel and the page keys.
        KeyCommand.TerminalCopy => [TerminalChord(KeyboardKey.C)],
        KeyCommand.TerminalPaste => [TerminalChord(KeyboardKey.V)],
        KeyCommand.TerminalSelectAll => [TerminalChord(KeyboardKey.A)],
        KeyCommand.TerminalPageUp => [new(KeyboardKey.PageUp, InputModifiers.Shift)],
        KeyCommand.TerminalPageDown => [new(KeyboardKey.PageDown, InputModifiers.Shift)],

        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "No default binding."),
    };

    private sealed class ReadOnly(KeyMap inner) : IKeyMap
    {
        public IReadOnlyList<KeyGesture> GesturesFor(KeyCommand command) => inner.GesturesFor(command);

        public bool Matches(KeyCommand command, KeyboardKey key, InputModifiers modifiers) =>
            inner.Matches(command, key, modifiers);

        public string Display(KeyCommand command) => inner.Display(command);
    }

    private static KeyGesture[] RepoHotkey(KeyboardKey digit, KeyboardKey numpad) =>
        [KeyGesture.WithPrimary(digit), KeyGesture.WithPrimary(numpad)];

    private static KeyGesture TerminalChord(KeyboardKey key) => OperatingSystem.IsMacOS()
        ? new KeyGesture(key, InputModifiers.Super)
        : new KeyGesture(key, InputModifiers.Control | InputModifiers.Shift);
}
