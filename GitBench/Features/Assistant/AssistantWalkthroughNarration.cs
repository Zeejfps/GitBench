using GitBench.Features.Assistant.Tools;
using GitBench.Features.Review;
using GitBench.Features.Review.Walkthrough;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// The built-in assistant as one repository's walkthrough narrator: turns the review window's cues
/// into turns of a thread the walkthrough agent keeps across them, and routes what the agent says
/// outside its tool calls to the rail as the current step's exchange. UI thread only.
/// </summary>
/// <remarks>
/// A cue that lands while the repository's conversation is mid-turn — the reviewer clicked Next
/// past the frontier before the agent finished writing, or is chatting in the main window — is
/// held, latest wins, and sent the moment the turn ends: the assistant's equivalent of the store's
/// latch, so a click is never lost. "Walk me through this" starts the thread over; the stops of a
/// walkthrough that ended are not context for the next one.
/// </remarks>
internal sealed class AssistantWalkthroughNarration : IDisposable
{
    private const string BeginAsk = "Walk me through this change.";

    private readonly Guid _repoId;
    private readonly AssistantSession _session;
    private readonly Func<Action<AssistantEvent>, AssistantThread> _threads;
    private readonly IReviewWindowRegistry _windows;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDisposable _busySub;

    private AssistantThread? _thread;
    private WalkthroughCue? _pending;
    private bool _sawBusy;
    private bool _disposed;

    /// <param name="threads">Builds a fresh thread for the walkthrough agent, reporting to the
    /// observer given — the store's to build, since the loop needs its backend and toolset.</param>
    public AssistantWalkthroughNarration(
        Guid repoId,
        AssistantSession session,
        Func<Action<AssistantEvent>, AssistantThread> threads,
        IReviewWindowRegistry windows,
        IUiDispatcher dispatcher)
    {
        _repoId = repoId;
        _session = session;
        _threads = threads;
        _windows = windows;
        _dispatcher = dispatcher;
        _busySub = session.IsBusy.Subscribe(OnBusyChanged);
    }

    /// <summary>Sends the cue as the agent's next turn, or holds it until the running one ends.</summary>
    public void Cue(WalkthroughCue cue)
    {
        if (_disposed) return;
        if (_session.IsBusy.Value)
        {
            _pending = cue;
            return;
        }

        Run(cue);
    }

    /// <summary>The provider changed under the thread: what it remembers was said to another
    /// model, whose tool-call ids the new one would refuse, so the next cue starts a new thread.</summary>
    public void RestartForProviderChange() => _thread = null;

    private void Run(WalkthroughCue cue)
    {
        if (cue is WalkthroughCue.Begin || _thread is null)
            _thread = _threads(Observe);
        _session.RunThread(Prompt(cue), _thread);
    }

    // Addressed to the model, so written in English like every other preset's ask; the reply
    // language rides in the turn's context block.
    private static string Prompt(WalkthroughCue cue) => cue switch
    {
        WalkthroughCue.Begin => BeginAsk,
        WalkthroughCue.Next next =>
            $"The reviewer stepped past step {WalkthroughTools.WireIndex(next.At)}. Continue with the next stops, "
            + "or end the walkthrough with a summary if the change has been covered.",
        WalkthroughCue.Ask ask => Question(ask),
        _ => throw new ArgumentOutOfRangeException(nameof(cue), cue, "Unknown cue."),
    };

    private static string Question(WalkthroughCue.Ask ask)
    {
        var line = $"At step {WalkthroughTools.WireIndex(ask.At)} the reviewer asks: {ask.Question}";
        return ask.Selection is { } selection ? selection.ToPrompt(line) : line;
    }

    // Fires from inside the session's own turn-end, so the held cue is posted rather than sent
    // here: a turn started from there would be half torn down by the rest of that turn-end. The
    // subscription's opening call is not a turn-end — the rail is already up for the first cue,
    // and a "finished" then would take it down before the turn had begun.
    private void OnBusyChanged(bool busy)
    {
        if (busy) _sawBusy = true;
        if (busy || _disposed || !_sawBusy) return;

        if (_pending is not null)
        {
            _dispatcher.Post(SendPending);
            return;
        }

        Store()?.MarkNarratorWaiting();
    }

    private void SendPending()
    {
        if (_disposed || _pending is not { } cue) return;
        _pending = null;
        Cue(cue);
    }

    // The agent's prose lands under the step the reviewer is on as it streams; a step call in the
    // same turn moves the current step, so what follows it lands under the new one. A turn that
    // died says so in the same place, since the rail is the assistant's only surface in the window.
    private void Observe(AssistantEvent e)
    {
        switch (e)
        {
            case AssistantEvent.TextDelta delta:
                Store()?.AppendNarration(delta.Text);
                break;
            case AssistantEvent.Failed failed:
                Store()?.ReportFailure(failed.Message);
                break;
            case AssistantEvent.Refused refused:
                Store()?.ReportRefusal(refused.Explanation ?? string.Empty);
                break;
            case AssistantEvent.Thinking:
            case AssistantEvent.ToolStarted:
            case AssistantEvent.ToolFinished:
            case AssistantEvent.NoToolSupport:
            case AssistantEvent.Completed:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(e), e, "Unknown assistant event.");
        }
    }

    // The window the repository's walkthrough tools drive.
    private ReviewWalkthroughStore? Store() => _windows.LatestFor(_repoId)?.Walkthrough;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _busySub.Dispose();
    }
}
