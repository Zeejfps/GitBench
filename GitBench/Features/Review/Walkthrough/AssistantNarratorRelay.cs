using GitBench.Messages;

namespace GitBench.Features.Review.Walkthrough;

/// <summary>
/// One review window's line to the built-in assistant as narrator: the reviewer's request for a
/// walkthrough, and the moves the store raises while the assistant is driving, go out as
/// <see cref="NarrateWalkthroughMessage"/>s for the window's repository. The store itself knows
/// nothing of the bus; this is the whole of what it takes to reach the assistant from a window.
/// </summary>
internal sealed class AssistantNarratorRelay : IDisposable
{
    private readonly ReviewWalkthroughStore _store;
    private readonly Guid _repoId;
    private readonly IMessageBus _bus;

    public AssistantNarratorRelay(ReviewWalkthroughStore store, Guid repoId, IMessageBus bus)
    {
        _store = store;
        _repoId = repoId;
        _bus = bus;
        _store.AssistantCued += Relay;
    }

    /// <summary>Asks the assistant to walk the reviewer through the window's change.</summary>
    public void Begin() => _bus.Broadcast(new NarrateWalkthroughMessage(_repoId, new WalkthroughCue.Begin()));

    private void Relay(WalkthroughCue.Move move) => _bus.Broadcast(new NarrateWalkthroughMessage(_repoId, move));

    public void Dispose() => _store.AssistantCued -= Relay;
}
