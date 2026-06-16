using GitBench.Features.Repos;
using GitBench.Messages;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.App;

internal sealed class AppKeybindController : KeyboardMouseController
{
    private readonly IRepoRegistry _registry;
    private readonly IMessageBus _bus;

    public AppKeybindController(IRepoRegistry registry, IMessageBus bus)
    {
        _registry = registry;
        _bus = bus;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;
        if (e.Key != KeyboardKey.F5) return;

        var repo = _registry.Active.Value;
        if (repo == null) return;

        // Replays the two bus messages that every refresh-on-change subscriber already
        // reacts to (CommitsPresenter, LocalChangesViewModel, BranchesViewModel,
        // ActionsToolbarViewModel, …). Equivalent to "pretend something just changed",
        // which is exactly what a forced refresh is.
        _bus.Broadcast(new RefsChangedMessage(repo.Id));
        _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
        e.Consume();
    }
}