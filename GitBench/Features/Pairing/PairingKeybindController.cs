using GitBench.Input;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Pairing;

/// <summary>
/// The pairing stop's chords — Accept, Accept &amp; next, Next — for the session of the repository
/// on screen, from anywhere in the main window. On the way back out, so a field that takes the
/// chord for itself keeps it; the editor lets Ctrl/Cmd+Enter through.
/// </summary>
internal sealed class PairingKeybindController : KeyboardMouseController
{
    private readonly IKeyMap _keys;
    private readonly PairingSessions _sessions;

    public PairingKeybindController(IKeyMap keys, PairingSessions sessions)
    {
        _keys = keys;
        _sessions = sessions;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed || e.Phase != EventPhase.Bubbling) return;
        if (_sessions.Active.Value?.Store is not { Stop.Value: not null } store) return;

        if (_keys.Matches(KeyCommand.PairingAcceptAndNext, e.Key, e.Modifiers))
        {
            _ = store.AcceptAndNextAsync();
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.PairingAccept, e.Key, e.Modifiers))
        {
            _ = store.AcceptAsync();
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.PairingNext, e.Key, e.Modifiers))
        {
            _ = store.DoneAsync();
            e.Consume();
        }
    }
}
