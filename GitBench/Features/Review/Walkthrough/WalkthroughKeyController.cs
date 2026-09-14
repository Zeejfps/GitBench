using GitBench.Input;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.Review.Walkthrough;

/// <summary>
/// The walkthrough rail's keys, live only while the rail is showing: Next and Back step the
/// walkthrough, Ask hands the caret to the rail's question field. Handled on bubbling, so a field
/// being typed into has already claimed its own letters and Space before this sees them.
/// </summary>
internal sealed class WalkthroughKeyController : KeyboardMouseController
{
    private readonly ReviewWalkthroughStore _store;
    private readonly IKeyMap _keys;
    private readonly IReadable<bool> _suspended;

    /// <param name="suspended">While true the keys are left alone — the review window's cheatsheet
    /// owns them then.</param>
    public WalkthroughKeyController(ReviewWalkthroughStore store, IKeyMap keys, IReadable<bool> suspended)
    {
        _store = store;
        _keys = keys;
        _suspended = suspended;
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.State != InputState.Pressed) return;
        if (!_store.IsVisible.Value || _suspended.Value) return;

        if (_keys.Matches(KeyCommand.WalkthroughNext, e.Key, e.Modifiers))
        {
            _store.Next();
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.WalkthroughBack, e.Key, e.Modifiers))
        {
            _store.Back();
            e.Consume();
        }
        else if (_keys.Matches(KeyCommand.WalkthroughAsk, e.Key, e.Modifiers))
        {
            _store.RequestAskFocus();
            e.Consume();
        }
    }
}
