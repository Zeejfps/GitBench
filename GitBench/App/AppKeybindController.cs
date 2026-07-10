using GitBench.Features.Notifications;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.App;

internal sealed class AppKeybindController : KeyboardMouseController
{
    // The "switch to tab N" modifier: Cmd on macOS, Ctrl elsewhere. Lock keys (Caps/Num) are masked
    // out so the gesture still matches with them on.
    private static readonly InputModifiers PrimaryModifier =
        OperatingSystem.IsMacOS() ? InputModifiers.Super : InputModifiers.Control;

    private const InputModifiers RelevantMask =
        InputModifiers.Shift | InputModifiers.Control | InputModifiers.Alt | InputModifiers.Super;

    private readonly IRepoRegistry _registry;
    private readonly RepoHoverState _hover;
    private readonly RepoBarCollapseState _repoBarCollapse;
    private readonly ILocalizationService _loc;
    private readonly IMessageBus _bus;

    public AppKeybindController(
        IRepoRegistry registry,
        RepoHoverState hover,
        RepoBarCollapseState repoBarCollapse,
        ILocalizationService loc,
        IMessageBus bus)
    {
        _registry = registry;
        _hover = hover;
        _repoBarCollapse = repoBarCollapse;
        _loc = loc;
        _bus = bus;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;

        if (e.Key == KeyboardKey.F5)
        {
            ForceRefresh();
            e.Consume();
            return;
        }

        if (e.Key == KeyboardKey.B && (e.Modifiers & RelevantMask) == PrimaryModifier)
        {
            _repoBarCollapse.Toggle();
            e.Consume();
            return;
        }

        if (DigitFromKey(e.Key) is { } slot && (e.Modifiers & RelevantMask) == PrimaryModifier)
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

    private static int? DigitFromKey(KeyboardKey key) => key switch
    {
        KeyboardKey.Alpha1 or KeyboardKey.Numpad1 => 1,
        KeyboardKey.Alpha2 or KeyboardKey.Numpad2 => 2,
        KeyboardKey.Alpha3 or KeyboardKey.Numpad3 => 3,
        KeyboardKey.Alpha4 or KeyboardKey.Numpad4 => 4,
        KeyboardKey.Alpha5 or KeyboardKey.Numpad5 => 5,
        KeyboardKey.Alpha6 or KeyboardKey.Numpad6 => 6,
        KeyboardKey.Alpha7 or KeyboardKey.Numpad7 => 7,
        KeyboardKey.Alpha8 or KeyboardKey.Numpad8 => 8,
        KeyboardKey.Alpha9 or KeyboardKey.Numpad9 => 9,
        _ => null,
    };
}
