using System.Runtime.ExceptionServices;
using GitBench.Features.Diff;
using ZGF.Observable;

namespace GitBench.Features.Review.Walkthrough;

/// <summary>
/// One review window's guided walkthrough: the steps a narrator has sent, the one the reviewer is
/// on, and the hand-off between them. A narrator shows a batch and waits; the reviewer walks the
/// batch locally and the wait completes only when they step past the frontier, ask a question, or
/// the bounded wait runs out. Back never reaches the narrator. Every member is UI-thread only; the
/// wait's task is what crosses to a tool thread.
/// </summary>
/// <remarks>
/// One waiter ever: a newer wait, batch, end or disconnect cancels the one attached. One latch
/// ever: an action with no waiter attached is held and handed to the next wait at once, and a later
/// action replaces it — a click is never lost, and a newer intent supersedes an older one. A new
/// batch drops the latch: whatever was latched is what the narrator just answered with the batch.
///
/// The waiter and the latch are the MCP session's channel. The built-in assistant never waits: its
/// step call returns at once and the reviewer's move has to become its next user turn, so for an
/// assistant narrator a frontier Next or an Ask is raised as a <see cref="WalkthroughCue.Move"/>
/// on <see cref="AssistantCued"/> instead of being latched, and whoever runs the assistant carries
/// it there.
///
/// A walkthrough the reviewer asks for is up from the asking: <see cref="Begin"/> puts a
/// <see cref="WalkthroughCard.Preparing"/> card in the rail with the narrator marked as working, and
/// what the narrator says before its first step — or the failure it dies with — is that card's
/// exchange, so the wait is never a blank window. An MCP narrator never begins this way; its first
/// batch is the first the rail hears of it.
///
/// Every step carries its own exchange — the questions asked on it and what the narrator said
/// outside its tool calls, in order — so Back re-shows a step's conversation with it. The
/// narrator's prose streams onto one message per turn: a new turn, a question, or a move to
/// another step starts a new one.
/// </remarks>
internal sealed class ReviewWalkthroughStore : IDisposable
{
    /// <summary>How long one wait blocks before answering <see cref="WalkthroughAction.Pending"/>.
    /// Under the per-call timeout MCP clients enforce, so a wait on a slow reader is re-entered
    /// instead of killed.</summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private const int NoStep = -1;

    private readonly IReviewPresentation _presentation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _clock;

    private readonly State<IReadOnlyList<WalkthroughStep>> _steps = new(Array.Empty<WalkthroughStep>());
    private readonly State<int> _current = new(NoStep);
    private readonly State<int> _frontier = new(NoStep);
    private readonly State<WalkthroughPhase> _phase = new(new WalkthroughPhase.Idle());
    private readonly State<string?> _summary = new(null);
    private readonly State<ReviewLineResolution?> _lastFailure = new(null);
    private readonly Derived<WalkthroughCard> _card;
    private readonly Derived<bool> _isVisible;
    private readonly Derived<bool> _isStarting;
    private readonly Derived<bool> _canGoBack;
    private readonly Derived<bool> _canGoNext;
    private readonly Derived<ObservableList<WalkthroughMessage>> _exchange;
    private readonly Derived<bool> _isComposing;

    // One exchange per step, parallel to _steps, and one for the preparing card; the narration
    // still being streamed, with the step it lands on, so a fragment for another step or another
    // turn starts a message of its own.
    private readonly ObservableList<WalkthroughMessage> _preparingExchange = new();
    private readonly List<ObservableList<WalkthroughMessage>> _exchanges = new();
    private readonly State<OpenNarration?> _open = new(null);

    private Waiter? _waiter;
    private WalkthroughAction? _latched;

    // Bumped per presentation apply, so a slow focus for a step already left never reports on it.
    private int _applyGeneration;
    private CancellationTokenSource? _applyCts;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    public ReviewWalkthroughStore(IReviewPresentation presentation, IUiDispatcher dispatcher, TimeProvider clock)
    {
        _presentation = presentation;
        _dispatcher = dispatcher;
        _clock = clock;
        _card = new Derived<WalkthroughCard>(BuildCard);
        _isVisible = new Derived<bool>(() => _card.Value is not WalkthroughCard.None);
        _isStarting = new Derived<bool>(() =>
            _card.Value is WalkthroughCard.Preparing && _phase.Value is WalkthroughPhase.Narrating);
        _canGoBack = new Derived<bool>(() => _current.Value > 0);
        _canGoNext = new Derived<bool>(() =>
            _current.Value != NoStep && _phase.Value is WalkthroughPhase.Narrating);
        _exchange = new Derived<ObservableList<WalkthroughMessage>>(() =>
        {
            var current = _current.Value;
            return current != NoStep && current < _exchanges.Count ? _exchanges[current] : _preparingExchange;
        });
        _isComposing = new Derived<bool>(() =>
            _phase.Value is WalkthroughPhase.Narrating { Waiting: false }
            && (_open.Value is not { } open || open.Step != _current.Value));
    }

    /// <summary>Every step shown so far, in order; Back walks this list.</summary>
    public IReadable<IReadOnlyList<WalkthroughStep>> Steps => _steps;

    /// <summary>The step the reviewer is on, 0-based; -1 while none is showing.</summary>
    public IReadable<int> Current => _current;

    /// <summary>The last step the narrator has sent; a Next past it hands control back.</summary>
    public IReadable<int> Frontier => _frontier;

    public IReadable<WalkthroughPhase> Phase => _phase;

    /// <summary>What the rail renders for the reviewer's position.</summary>
    public IReadable<WalkthroughCard> Card => _card;

    /// <summary>Whether there is anything for the rail to show.</summary>
    public IReadable<bool> IsVisible => _isVisible;

    /// <summary>True from <see cref="Begin"/> until the narrator's first step, its giving up, or a
    /// Clear — while asking again would only queue a second walkthrough behind this one.</summary>
    public IReadable<bool> IsStarting => _isStarting;

    public IReadable<bool> CanGoBack => _canGoBack;

    /// <summary>Next is live while a narrator is driving; once it has gone away nothing would answer.</summary>
    public IReadable<bool> CanGoNext => _canGoNext;

    /// <summary>The exchange under the step the reviewer is on — or, before the first step, under
    /// the preparing card: their questions and the narrator's prose outside its tool calls, in order.</summary>
    public IReadable<ObservableList<WalkthroughMessage>> Exchange => _exchange;

    /// <summary>True while the narrator is at work and nothing of what it is writing has landed on
    /// the current step yet — where the rail shows it thinking.</summary>
    public IReadable<bool> IsComposing => _isComposing;

    /// <summary>The last focus or spotlight the diff could not land, for the rail's notice; null once
    /// a later step's presentation succeeds.</summary>
    public IReadable<ReviewLineResolution?> LastFailure => _lastFailure;

    /// <summary>The reviewer's current selection, so the rail can hint that an Ask will carry it.</summary>
    public IReadable<DiffSelectionQuote?> Selection => _presentation.Selection;

    /// <summary>Raised when a key asks the rail's Ask field to take the caret.</summary>
    public event Action? AskFocusRequested;

    /// <summary>Raised, while the built-in assistant narrates, with the reviewer's move it has to
    /// answer: a Next past the frontier or a question. Never raised for an MCP narrator, whose
    /// moves answer its wait.</summary>
    public event Action<WalkthroughCue.Move>? AssistantCued;

    /// <summary>The reviewer asked <paramref name="narrator"/> for a walkthrough: whatever was up
    /// goes, and the rail shows the narrator at work until its first batch arrives.</summary>
    public void Begin(Narrator narrator)
    {
        ThrowIfDisposed();
        var previousAt = _current.Value;
        Reset();
        _summary.Value = null;
        _phase.Value = new WalkthroughPhase.Narrating(narrator, Waiting: false);
        ResolveWaiter(new WalkthroughAction.Cancelled(previousAt));
    }

    /// <summary>Appends a batch and shows its first step. Supersedes whatever wait was attached
    /// (it answers <see cref="WalkthroughAction.Cancelled"/>) and drops any latched action.</summary>
    public void Show(Narrator narrator, IReadOnlyList<WalkthroughStep> batch)
    {
        if (batch.Count == 0) throw new ArgumentException("A batch holds at least one step.", nameof(batch));
        ThrowIfDisposed();

        var previousAt = _current.Value;
        var combined = new List<WalkthroughStep>(_steps.Value.Count + batch.Count);
        combined.AddRange(_steps.Value);
        var first = combined.Count;
        combined.AddRange(batch);

        _summary.Value = null;
        for (var i = 0; i < batch.Count; i++) _exchanges.Add(new ObservableList<WalkthroughMessage>());
        _steps.Value = combined;
        _frontier.Value = combined.Count - 1;
        _current.Value = first;
        _phase.Value = new WalkthroughPhase.Narrating(narrator, Waiting: false);
        _latched = null;
        ResolveWaiter(new WalkthroughAction.Cancelled(previousAt));
        Apply(batch[0]);
    }

    /// <summary>Blocks until the reviewer steps past the frontier or asks, up to
    /// <see cref="WaitTimeout"/>. A latched action answers at once. Attaching while another wait
    /// is attached cancels that one. <paramref name="ct"/> answers <see cref="WalkthroughAction.Cancelled"/>
    /// and detaches.</summary>
    public Task<WalkthroughAction> WaitAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        if (_latched is { } latched)
        {
            _latched = null;
            return Task.FromResult(latched);
        }

        if (ct.IsCancellationRequested)
            return Task.FromResult<WalkthroughAction>(new WalkthroughAction.Cancelled(_current.Value));

        ResolveWaiter(new WalkthroughAction.Cancelled(_current.Value));

        var waiter = new Waiter(_current.Value);
        _waiter = waiter;
        SetWaiting(true);
        waiter.Timer = _clock.CreateTimer(
            _ => _dispatcher.Post(() => OnTimeout(waiter)), null, WaitTimeout, Timeout.InfiniteTimeSpan);
        // The caller's cancellation may fire on any thread: answer the task there (it is thread-safe)
        // so the caller is never left hanging on a dispatcher that has gone quiet, and detach here.
        waiter.Registration = ct.Register(() =>
        {
            waiter.Completion.TrySetResult(new WalkthroughAction.Cancelled(waiter.AttachedAt));
            _dispatcher.Post(() => Detach(waiter));
        });
        return waiter.Completion.Task;
    }

    /// <summary>Forward: locally while steps remain in the batch; past the frontier it is the
    /// narrator's cue.</summary>
    public void Next()
    {
        if (_current.Value == NoStep) return;
        if (_current.Value < _frontier.Value)
        {
            _current.Value++;
            Apply(_steps.Value[_current.Value]);
            return;
        }

        if (_phase.Value is not WalkthroughPhase.Narrating narrating) return;
        Deliver(narrating.Who, new WalkthroughCue.Next(_current.Value));
    }

    /// <summary>Re-shows the previous step; never reaches the narrator.</summary>
    public void Back()
    {
        if (_current.Value <= 0) return;
        _current.Value--;
        Apply(_steps.Value[_current.Value]);
    }

    /// <summary>Sends a question about the current step, with the reviewer's selection if any; the
    /// question joins the step's exchange, and the answer starts a message of its own after it.</summary>
    public void Ask(string question)
    {
        if (_current.Value == NoStep || string.IsNullOrWhiteSpace(question)) return;
        if (_phase.Value is not WalkthroughPhase.Narrating narrating) return;
        var selection = _presentation.Selection.Value;
        var asked = question.Trim();
        _open.Value = null;
        _exchange.Value.Add(new WalkthroughMessage.Question(asked, selection));
        Deliver(narrating.Who, new WalkthroughCue.Ask(_current.Value, asked, selection));
    }

    /// <summary>Re-focuses one of the current step's spotlights (a click on its note).</summary>
    public void FocusSpotlight(int index)
    {
        if (_card.Value is not WalkthroughCard.Step step) return;
        if (index < 0 || index >= step.Content.Spotlights.Count) return;
        var spotlight = step.Content.Spotlights[index];
        Track(ObserveAsync(
            _presentation.FocusLineAsync(new ReviewLineRef(spotlight.Path, spotlight.Side, spotlight.From), CancellationToken.None),
            _applyGeneration));
    }

    /// <summary>Ends the walkthrough: steps and spotlights go, the wait (if any) is cancelled, and
    /// a summary, when given, stays up as a closing card until the reviewer clears it.</summary>
    public void End(string? summaryMarkdown)
    {
        var previousAt = _current.Value;
        Reset();
        _summary.Value = string.IsNullOrWhiteSpace(summaryMarkdown) ? null : summaryMarkdown;
        ResolveWaiter(new WalkthroughAction.Cancelled(previousAt));
    }

    /// <summary>The narrator's session went away: the steps stay readable but nothing will answer a
    /// Next, so the rail offers Clear instead.</summary>
    public void MarkDisconnected()
    {
        if (_phase.Value is not WalkthroughPhase.Narrating narrating) return;
        _phase.Value = new WalkthroughPhase.Disconnected(narrating.Who);
        _latched = null;
        ResolveWaiter(new WalkthroughAction.Cancelled(_current.Value));
    }

    /// <summary>Drops everything the rail shows.</summary>
    public void Clear()
    {
        var previousAt = _current.Value;
        Reset();
        _summary.Value = null;
        ResolveWaiter(new WalkthroughAction.Cancelled(previousAt));
    }

    /// <summary>Streams the assistant's out-of-tool prose onto the current step — or, before its
    /// first step, onto the preparing card — continuing the message the turn is writing there, or
    /// opening one. A message opens on its first non-blank fragment: the whitespace a model puts
    /// ahead of its prose is nothing to read.</summary>
    public void AppendNarration(string text)
    {
        if (!AcceptsMessages) return;
        if (_open.Value is { } open && open.Step == _current.Value)
        {
            open.Text.Value += text;
            return;
        }

        var lead = text.TrimStart();
        if (lead.Length == 0) return;
        var opened = new OpenNarration(_current.Value, new State<string>(lead));
        _open.Value = opened;
        _exchange.Value.Add(new WalkthroughMessage.Narration(opened.Text));
    }

    /// <summary>The narrator's turn died: the failure joins the current exchange, where the
    /// reviewer is looking, since the rail is the assistant's only surface in the window.</summary>
    public void ReportFailure(string message)
    {
        if (!AcceptsMessages) return;
        _open.Value = null;
        _exchange.Value.Add(new WalkthroughMessage.Failure(message));
    }

    /// <summary>The narrator declined the turn, with <paramref name="explanation"/> as its reason.</summary>
    public void ReportRefusal(string explanation)
    {
        if (!AcceptsMessages) return;
        _open.Value = null;
        _exchange.Value.Add(new WalkthroughMessage.Refusal(explanation));
    }

    /// <summary>The built-in assistant's turn ended without it going away: whatever it was going
    /// to show is up, and the reviewer's next move is what happens next. A turn that ends before
    /// the first step is a narrator that is not going to give one: what it said stays up to be
    /// read, under a Clear; a silent one takes the rail down. Nothing to do for an MCP narrator,
    /// whose wait says this for it.</summary>
    public void MarkNarratorWaiting()
    {
        if (_phase.Value is not WalkthroughPhase.Narrating { Who.Kind: NarratorKind.Assistant } narrating) return;
        _open.Value = null;
        if (_card.Value is not WalkthroughCard.Preparing)
        {
            SetWaiting(true);
            return;
        }

        if (_preparingExchange.Count > 0) _phase.Value = new WalkthroughPhase.Disconnected(narrating.Who);
        else Clear();
    }

    public void RequestAskFocus() => AskFocusRequested?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
        _isComposing.Dispose();
        _exchange.Dispose();
        _canGoNext.Dispose();
        _canGoBack.Dispose();
        _isStarting.Dispose();
        _isVisible.Dispose();
        _card.Dispose();
    }

    private WalkthroughCard BuildCard()
    {
        if (_summary.Value is { } summary) return new WalkthroughCard.Finished(summary);
        var steps = _steps.Value;
        var current = _current.Value;
        if (current != NoStep && current < steps.Count) return new WalkthroughCard.Step(current, steps.Count, steps[current]);
        // Asked for and not yet stepped, or given up on before the first step: the card stands
        // until a step or a Clear.
        return _phase.Value is WalkthroughPhase.Idle ? new WalkthroughCard.None() : new WalkthroughCard.Preparing();
    }

    // A message has somewhere to land while a step is up, and while a narrator is composing its first.
    private bool AcceptsMessages => _current.Value != NoStep || _phase.Value is WalkthroughPhase.Narrating;

    // Clears the steps, their exchanges and their presentation; the phase goes idle. The summary is
    // the caller's.
    private void Reset()
    {
        CancelApply();
        _latched = null;
        _open.Value = null;
        _steps.Value = Array.Empty<WalkthroughStep>();
        _current.Value = NoStep;
        _exchanges.Clear();
        _preparingExchange.Clear();
        _frontier.Value = NoStep;
        _phase.Value = new WalkthroughPhase.Idle();
        _lastFailure.Value = null;
        _presentation.ClearSpotlights();
    }

    // Hands the reviewer's move to whoever narrates. An MCP session gets it through its wait —
    // resolved if one is attached, latched for the next one otherwise, latest wins. The assistant
    // gets it raised as a cue, and is composing from then until its turn ends; what it writes in
    // answer is a message of its own.
    private void Deliver(Narrator who, WalkthroughCue.Move cue)
    {
        switch (who.Kind)
        {
            case NarratorKind.McpSession:
                var action = ToAction(cue);
                if (_waiter != null) ResolveWaiter(action);
                else _latched = action;
                return;

            case NarratorKind.Assistant:
                _open.Value = null;
                SetWaiting(false);
                AssistantCued?.Invoke(cue);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(who), who.Kind, "Unknown narrator kind.");
        }
    }

    private static WalkthroughAction ToAction(WalkthroughCue.Move cue) => cue switch
    {
        WalkthroughCue.Next next => new WalkthroughAction.Next(next.At),
        WalkthroughCue.Ask ask => new WalkthroughAction.Ask(ask.At, ask.Question, ask.Selection),
        _ => throw new ArgumentOutOfRangeException(nameof(cue), cue, "Unknown cue."),
    };

    private void ResolveWaiter(WalkthroughAction action)
    {
        if (_waiter is not { } waiter) return;
        Detach(waiter);
        waiter.Completion.TrySetResult(action);
    }

    private void OnTimeout(Waiter waiter)
    {
        if (_waiter != waiter) return;
        Detach(waiter);
        waiter.Completion.TrySetResult(new WalkthroughAction.Pending(_current.Value));
    }

    private void Detach(Waiter waiter)
    {
        waiter.Timer?.Dispose();
        waiter.Registration.Dispose();
        if (_waiter != waiter) return;
        _waiter = null;
        SetWaiting(false);
    }

    private void SetWaiting(bool waiting)
    {
        if (_phase.Value is WalkthroughPhase.Narrating narrating && narrating.Waiting != waiting)
            _phase.Value = narrating with { Waiting = waiting };
    }

    // Focus first, so the file is active and loaded, then the spotlights; a failed resolution is
    // kept for the rail rather than thrown at whoever stepped. The presentation is UI-thread only
    // and its tasks may complete on whichever thread laid the line out (nothing here installs a
    // synchronization context), so every call into it is made from the UI thread and every result
    // is stepped back onto it before it touches state.
    private void Apply(WalkthroughStep step)
    {
        CancelApply();
        var generation = ++_applyGeneration;
        var cts = new CancellationTokenSource();
        _applyCts = cts;
        _lastFailure.Value = null;
        if (step.Focus is { } focus)
            Track(FocusThenSpotlightAsync(step, focus, generation, cts.Token));
        else
            ApplySpotlights(step, generation, cts.Token);
    }

    private async Task FocusThenSpotlightAsync(WalkthroughStep step, ReviewLineRef focus, int generation, CancellationToken ct)
    {
        ReviewLineResolution focused;
        try
        {
            focused = await _presentation.FocusLineAsync(focus, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A later step superseded this one; its own apply reports for it.
            return;
        }

        OnUiThread(() =>
        {
            if (!IsLive(generation)) return;
            Record(focused);
            ApplySpotlights(step, generation, ct);
        });
    }

    private void ApplySpotlights(WalkthroughStep step, int generation, CancellationToken ct)
    {
        if (step.Spotlights.Count == 0)
        {
            _presentation.ClearSpotlights();
            return;
        }

        Track(ObserveAsync(_presentation.SetSpotlightsAsync(step.Spotlights, step.Dim, ct), generation));
    }

    private async Task ObserveAsync(Task<IReadOnlyList<ReviewLineResolution>> pending, int generation)
    {
        IReadOnlyList<ReviewLineResolution> resolutions;
        try
        {
            resolutions = await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        OnUiThread(() =>
        {
            if (!IsLive(generation)) return;
            foreach (var resolution in resolutions) Record(resolution);
        });
    }

    private async Task ObserveAsync(Task<ReviewLineResolution> pending, int generation)
    {
        ReviewLineResolution resolved;
        try
        {
            resolved = await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        OnUiThread(() =>
        {
            if (IsLive(generation)) Record(resolved);
        });
    }

    private bool IsLive(int generation) => generation == _applyGeneration && !_disposed;

    // Cancellation is the one outcome these tasks handle themselves; anything else is a bug in the
    // presentation, and it surfaces on the UI thread rather than dying unobserved with the task.
    private void Track(Task task) => task.ContinueWith(
        faulted =>
        {
            if (faulted.Exception is not { } error) return;
            var inner = error.InnerExceptions.Count == 1 ? error.InnerExceptions[0] : error;
            _dispatcher.Post(() => ExceptionDispatchInfo.Capture(inner).Throw());
        },
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private void Record(ReviewLineResolution resolution)
    {
        if (resolution is ReviewLineResolution.Resolved) return;
        _lastFailure.Value = resolution;
    }

    private void OnUiThread(Action action)
    {
        if (Environment.CurrentManagedThreadId == _uiThreadId) action();
        else _dispatcher.Post(action);
    }

    // Cancelled, not disposed: the presentation may still be registering on the token, and a
    // timer-less source holds nothing worth reclaiming early.
    private void CancelApply()
    {
        _applyGeneration++;
        _applyCts?.Cancel();
        _applyCts = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ReviewWalkthroughStore));
    }

    // The narration a turn is streaming, and the step it opened on.
    private sealed record OpenNarration(int Step, State<string> Text);

    private sealed class Waiter
    {
        public Waiter(int attachedAt) => AttachedAt = attachedAt;

        public int AttachedAt { get; }

        public TaskCompletionSource<WalkthroughAction> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ITimer? Timer { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
    }
}
