using GitBench.Features.Assistant;
using GitBench.Features.FileBrowser;
using GitBench.Features.Notifications;
using GitBench.Features.AgentConnections.Acp;
using GitBench.Features.Pairing;
using GitBench.Features.Repos;
using GitBench.Features.Search;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.App;

internal sealed class AppKeybindController : KeyboardMouseController, IDisposable
{
    private readonly IKeyMap _keys;
    private readonly IRepoRegistry _registry;
    private readonly RepoHoverState _hover;
    private readonly RepoBarCollapseState _repoBarCollapse;
    private readonly ILocalizationService _loc;
    private readonly IMessageBus _bus;
    private readonly AssistantViewModel _assistant;
    private readonly AgentChat _chat;
    private readonly State<MainViewMode> _mode;
    private readonly IFileBrowserStore _browsers;
    private readonly State<SidebarPane> _sidebar;
    private readonly SearchEverywhereViewModel _search;
    private readonly InputSystem _input;
    private readonly KeySequenceRecognizer _sequences;

    public AppKeybindController(
        IKeyMap keys,
        IRepoRegistry registry,
        RepoHoverState hover,
        RepoBarCollapseState repoBarCollapse,
        ILocalizationService loc,
        IMessageBus bus,
        AssistantViewModel assistant,
        AgentChat chat,
        State<MainViewMode> mode,
        IFileBrowserStore browsers,
        State<SidebarPane> sidebar,
        SearchEverywhereViewModel search,
        InputSystem input,
        TimeProvider time)
    {
        _keys = keys;
        _sidebar = sidebar;
        _registry = registry;
        _hover = hover;
        _repoBarCollapse = repoBarCollapse;
        _loc = loc;
        _bus = bus;
        _assistant = assistant;
        _chat = chat;
        _mode = mode;
        _browsers = browsers;
        _search = search;
        _input = input;
        _sequences = new KeySequenceRecognizer(keys, time, RunSequence);
        input.AddFilter(_sequences);
    }

    public void Dispose() => _input.RemoveFilter(_sequences);

    // Only commands that make sense from anywhere in the window are run here; a trigger bound to a
    // pane's command is that pane's to act on, which none is yet.
    private void RunSequence(KeyCommand command)
    {
        if (command == KeyCommand.SearchEverywhere) _search.Open();
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;

        if (_keys.Matches(KeyCommand.Refresh, e.Key, e.Modifiers))
        {
            ForceRefresh();
            e.Consume();
            return;
        }

        if (_keys.Matches(KeyCommand.ToggleRepoBar, e.Key, e.Modifiers))
        {
            _repoBarCollapse.Toggle();
            e.Consume();
            return;
        }

        if (_keys.Matches(KeyCommand.ToggleAssistant, e.Key, e.Modifiers))
        {
            // No menu to ask from here: the first agent on offer until one is picked.
            if (_chat.Press() is AgentChatPress.NeedsAgent) _chat.Open(AcpHarness.ClaudeCode);
            e.Consume();
            return;
        }

        if (_keys.Matches(KeyCommand.NewPairingSession, e.Key, e.Modifiers))
        {
            NewPairingSessionDialog.Show(_bus);
            e.Consume();
            return;
        }

        // Find in file. Here rather than on the pane, because the chord has to work the moment the
        // Files mode is on screen, and a controller down there is only sent keys once something
        // inside it has taken focus.
        if (_keys.Matches(KeyCommand.FindInFile, e.Key, e.Modifiers))
        {
            if (_mode.Value == MainViewMode.Files && _browsers.Active.Value is { CanSearch: true } browser)
            {
                browser.Search.Open();
                e.Consume();
            }
            return;
        }

        // Find a file by name. Swings the rail over to the files first: the chord means "take me to
        // a file", and refusing it because the branches happened to be showing would be a riddle.
        if (_keys.Matches(KeyCommand.FindFile, e.Key, e.Modifiers))
        {
            if (_browsers.Active.Value is { } files)
            {
                _sidebar.Value = SidebarPane.Files;
                files.Finder.Open();
                e.Consume();
            }
            return;
        }

        if (_keys.Matches(KeyCommand.SearchEverywhere, e.Key, e.Modifiers))
        {
            _search.Open();
            e.Consume();
            return;
        }

        // Esc closes the overlay only on the way back out: anything nearer the pointer — a dialog, a
        // rename field, a search bar — gets its own Esc first and consumes it.
        if (e.Key == KeyboardKey.Escape && e.Phase == EventPhase.Bubbling && _assistant.IsOpen.Value)
        {
            _assistant.Close.Execute();
            e.Consume();
            return;
        }

        if (RepoHotkeySlot(_keys, e.Key, e.Modifiers) is { } slot)
            HandleHotkey(slot, ref e);
    }

    private void ForceRefresh()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;

        // Replays the two bus messages that every refresh-on-change subscriber already reacts to
        // (CommitsPresenter, LocalChangesViewModel, BranchesViewModel, ActionsToolbarViewModel, …).
        // Equivalent to "pretend something just changed", which is exactly what a forced refresh is.
        _bus.Broadcast(new RefsChangedMessage(repo.Id));
        _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
    }

    private void HandleHotkey(int slot, ref KeyboardKeyEvent e)
    {
        var hovered = _hover.HoveredPrimary.Value;
        var holder = _registry.RepoForHotkey(slot);

        // Hovering a primary that doesn't already hold this slot → pin it; otherwise switch. So a press
        // while the pointer rests on the slot's own row (or on no row) activates instead of re-pinning.
        if (hovered is { } target && target != holder)
        {
            _registry.AssignHotkey(target, slot);
            var name = _registry.Repos.FirstOrDefault(r => r.Id == target)?.DisplayName ?? string.Empty;
            _bus.Broadcast(new ShowToastMessage(ToastIntent.Success(
                _loc.Strings.Value.ReposHotkeyAssigned(slot.ToString(), name))));
            e.Consume();
            return;
        }

        if (holder is { } repoId)
        {
            _registry.SetActive(repoId);
            // A collapsed group hides its rows; expand it so the repo you jumped to is visible.
            if (_registry.FindGroupContaining(repoId) is { IsCollapsed.Value: true } group)
                _registry.ToggleGroupCollapsed(group.Id);
            e.Consume();
        }
    }

    /// <summary>The repo hotkey slot this chord is bound to, or null. Shared with the terminal pane,
    /// which hands these chords back to the application.</summary>
    internal static int? RepoHotkeySlot(IKeyMap keys, KeyboardKey key, InputModifiers modifiers)
    {
        foreach (var command in KeyCommands.RepoHotkeys)
            if (keys.Matches(command, key, modifiers))
                return KeyCommands.RepoHotkeySlot(command);
        return null;
    }
}
