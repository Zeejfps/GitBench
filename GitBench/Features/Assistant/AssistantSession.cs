using GitBench.Features.Assistant.Backend;
using GitBench.Git;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// What one transcript row carries.
internal enum AssistantRowKind
{
    User,
    Reply,
    Tool,
    Approval,
    Error,
    Refusal,
    Notice,
}

/// <summary>
/// One entry of the visible transcript.
/// </summary>
/// <remarks>
/// A row is appended once and never replaced: streamed text and a tool call's outcome land on the
/// row's own observables, so a delta repaints that row instead of reseeding the list.
/// </remarks>
internal sealed class AssistantRow : IDisposable
{
    private readonly State<string> _text;
    private readonly State<bool> _running;
    private readonly State<bool> _failed;

    private AssistantRow(
        AssistantRowKind kind,
        string text,
        string? toolName,
        bool running,
        PendingToolApproval? pending = null)
    {
        Kind = kind;
        ToolName = toolName;
        Pending = pending;
        _text = new State<string>(text);
        _running = new State<bool>(running);
        _failed = new State<bool>(false);
    }

    public AssistantRowKind Kind { get; }

    /// <summary>The tool this row reports, for <see cref="AssistantRowKind.Tool"/> rows.</summary>
    public string? ToolName { get; }

    /// <summary>The run this row opened, for the first tool row of a run. The rest of the run lives
    /// inside it rather than in the transcript, so the reader sees one line for the whole thing.</summary>
    public AssistantToolGroup? Group { get; internal set; }

    /// <summary>The write waiting on an answer, for <see cref="AssistantRowKind.Approval"/> rows.</summary>
    public PendingToolApproval? Pending { get; }

    public IReadable<string> Text => _text;
    public IReadable<bool> IsRunning => _running;
    public IReadable<bool> Failed => _failed;

    public static AssistantRow User(string text) => new(AssistantRowKind.User, text, null, running: false);

    public static AssistantRow Reply() => new(AssistantRowKind.Reply, string.Empty, null, running: true);

    public static AssistantRow Tool(string name) => new(AssistantRowKind.Tool, string.Empty, name, running: true);

    public static AssistantRow Approval(PendingToolApproval pending) =>
        new(AssistantRowKind.Approval, string.Empty, pending.ToolName, running: false, pending);

    public static AssistantRow Error(string message) => new(AssistantRowKind.Error, message, null, running: false);

    /// <summary>A policy decline. The explanation is optional, so the row carries whatever the model
    /// gave and the view supplies the sentence around it.</summary>
    public static AssistantRow Refusal(string? explanation) =>
        new(AssistantRowKind.Refusal, explanation ?? string.Empty, null, running: false);

    /// <summary>Something about the exchange the reader should know, which is neither an answer nor
    /// a failure.</summary>
    public static AssistantRow Notice(string message) =>
        new(AssistantRowKind.Notice, message, null, running: false);

    public void Append(string text) => _text.Value += text;

    public void Finish(bool failed = false)
    {
        _running.Value = false;
        _failed.Value = failed;
    }

    public void Dispose()
    {
        Pending?.Dispose();
        _text.Dispose();
        _running.Dispose();
        _failed.Dispose();
    }
}

/// <summary>
/// One repository's assistant conversation: the transcript the user reads, the message list the
/// model sees, and the at-most-one turn in flight.
/// </summary>
/// <remarks>
/// A turn runs off the UI thread — its tools call straight into git — and every event is posted back
/// through the dispatcher, so the transcript is only ever mutated on the UI thread. A turn that ends
/// as anything but an answer is rolled out of the model's message list: the transcript keeps the
/// error, the model never sees the abandoned exchange.
///
/// A write tool suspends the turn on a <see cref="PendingToolApproval"/> that lives in the
/// transcript, so the pause belongs to the conversation rather than to whatever view happens to be
/// open. Stopping the turn while one is waiting withdraws it.
///
/// Clearing is the same move in reverse: the turn comes down first, then everything the exchange
/// left behind goes at once — the rows, the model's message list, the question nobody answered and
/// the folded tool runs — with one snapshot kept so the next thing the user does can be to undo it.
/// </remarks>
internal sealed class AssistantSession : IDisposable
{
    private readonly Repo _repo;
    private readonly AssistantAgentLoop _loop;
    private readonly ILocalizationService _loc;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<AssistantMessage> _conversation = [];
    private readonly Dictionary<string, AssistantRow> _toolRows = new(StringComparer.Ordinal);
    private readonly State<bool> _busy = new(false);
    private readonly State<bool> _thinking = new(false);
    private readonly ToolApprovalQueue _approvals;

    // Every row the session made, folded ones included, so disposal and clearing reach the rows that
    // live inside a tool run rather than only the ones the transcript shows.
    private readonly List<AssistantRow> _owned = [];
    private readonly List<AssistantToolGroup> _groups = [];

    private CancellationTokenSource? _turn;
    // What the running turn grows and which agent runs it. The thread's own list and loop for an
    // ordinary message; a throwaway list and a preset's loop for a one-shot ask.
    private List<AssistantMessage> _turnMessages;
    private AssistantAgentLoop _turnLoop;
    private AssistantRow? _reply;
    private PendingToolApproval? _pending;
    private AssistantToolGroup? _run;
    private ClearedConversation? _cleared;
    private bool _clearPending;
    private bool _restartPending;
    private int _turnStart;
    // Whether the turn reached an end the model could be asked to carry on from. A turn that did is
    // left in the message list even when it said nothing; anything else is rolled back out of it.
    private bool _resolved;
    // Whether the turn put anything in the transcript at all — an answer, a tool run, a question, an
    // advisory. Every append raises it, so a turn that ends with it still down is one the reader
    // watched produce nothing.
    private bool _produced;
    private bool _disposed;

    public AssistantSession(Repo repo, AssistantAgentLoop loop, ILocalizationService loc, IUiDispatcher dispatcher)
    {
        _repo = repo;
        _loop = loop;
        _loc = loc;
        _dispatcher = dispatcher;
        _approvals = new ToolApprovalQueue(dispatcher, Ask);
        _turnMessages = _conversation;
        _turnLoop = loop;
    }

    public Guid RepoId => _repo.Id;

    /// <summary>What the transcript draws. A run of tool calls contributes its first row only; the
    /// rest hang off that row's <see cref="AssistantRow.Group"/>.</summary>
    public ObservableList<AssistantRow> Rows { get; } = new();

    public IReadable<bool> IsBusy => _busy;

    /// <summary>True between the model starting to reason and its first visible output.</summary>
    public IReadable<bool> IsThinking => _thinking;

    /// <summary>Starts a turn. A send while one is running is dropped rather than queued.</summary>
    public void Send(string message) => Start(message, _loop, _conversation);

    /// <summary>
    /// Runs a one-shot agent — a diff selection's "Explain this" and its siblings — in this
    /// transcript without joining the thread.
    /// </summary>
    /// <remarks>
    /// The exchange goes into a list of its own, so the answer appears where the reader is looking
    /// but neither the question nor the reply steers the next thing they type. Detached is the whole
    /// point: "explain this" is asked about one selection, and carrying it forward would have every
    /// later answer reasoning from a fragment nobody is still looking at.
    /// </remarks>
    public void RunPreset(string prompt, AssistantAgentLoop loop) => Start(prompt, loop, []);

    private void Start(string message, AssistantAgentLoop loop, List<AssistantMessage> messages)
    {
        var text = message.Trim();
        if (text.Length == 0 || _busy.Value || _disposed) return;

        // A new message is the point of no return for the last clear: undoing it now would fold the
        // restored exchange under a question asked without it.
        DiscardCleared();
        Add(AssistantRow.User(text));
        _turnMessages = messages;
        _turnLoop = loop;
        _turnStart = messages.Count;
        messages.Add(new AssistantMessage.User(text));
        _reply = null;
        _resolved = false;
        _produced = false;
        _toolRows.Clear();
        _busy.Value = true;
        _thinking.Value = true;

        var cts = new CancellationTokenSource();
        _turn = cts;
        _ = Task.Run(() => RunAsync(cts));
    }

    public void Cancel() => _turn?.Cancel();

    /// <summary>
    /// Starts the conversation over: this repository's alone, since the store keys one thread per
    /// repository and the user relies on the others still being there.
    /// </summary>
    /// <remarks>
    /// A turn in flight is stopped first and the rows go only once it is down, so no stream is left
    /// writing into a transcript that no longer exists. What went is held for one
    /// <see cref="UndoClear"/> — the thread is unrecoverable otherwise, and a modal to confirm a
    /// chat wipe is heavier than the action deserves. The API key is untouched: it is not part of
    /// the conversation.
    /// </remarks>
    public void Clear()
    {
        if (_disposed) return;

        if (_busy.Value)
        {
            _clearPending = true;
            _turn?.Cancel();
            return;
        }

        ClearNow();
    }

    /// <summary>Puts back what the last <see cref="Clear"/> took, or calls off one that is still
    /// waiting on a turn to stop. A no-op once a new message has been sent.</summary>
    public void UndoClear()
    {
        if (_disposed) return;

        if (_clearPending)
        {
            _clearPending = false;
            return;
        }

        if (_cleared is not { } snapshot) return;
        _cleared = null;

        _owned.AddRange(snapshot.Owned);
        _groups.AddRange(snapshot.Groups);
        _conversation.AddRange(snapshot.Conversation);
        foreach (var row in snapshot.Visible) Rows.Add(row);
        _turnMessages = _conversation;
        _turnStart = _conversation.Count;
    }

    /// <summary>
    /// Marks the point where the provider changed: the transcript stays, and the exchange the model
    /// is sent starts again from here.
    /// </summary>
    /// <remarks>
    /// Nothing carries over because nothing safely can. A tool-call id belongs to the provider that
    /// issued it, and the whole conversation is replayed on every turn, so the next endpoint would
    /// be asked to accept ids it never made — which strict OpenAI-compatible endpoints refuse
    /// outright. A turn in flight is stopped first: its next step would otherwise be answered by a
    /// model the turn did not start on.
    /// </remarks>
    public void RestartForProviderChange(string notice)
    {
        if (_disposed) return;

        // Nothing was going to be replayed, so there is nothing to say about it not being.
        if (_conversation.Count > 0 || _busy.Value) Add(AssistantRow.Notice(notice));

        if (_busy.Value)
        {
            _restartPending = true;
            _turn?.Cancel();
            return;
        }

        RestartNow();
    }

    // The undo goes with the messages: putting a cleared exchange back would put back tool-call ids
    // the provider now in use never issued.
    private void RestartNow()
    {
        DiscardCleared();
        _conversation.Clear();
        _turnMessages = _conversation;
        _turnStart = 0;
        _resolved = false;
    }

    private void ClearNow()
    {
        DiscardCleared();
        _cleared = new ClearedConversation([.._owned], [.._groups], [..Rows], [.._conversation]);

        // The question and the fold state point at rows that are going, so they go with them.
        _pending?.Cancel();
        _pending = null;
        _toolRows.Clear();
        _reply = null;
        _run = null;

        Rows.Clear();
        _owned.Clear();
        _groups.Clear();
        _conversation.Clear();
        _turnMessages = _conversation;
        _turnStart = 0;
        _resolved = false;
    }

    private void DiscardCleared()
    {
        if (_cleared is not { } snapshot) return;
        _cleared = null;
        foreach (var group in snapshot.Groups) group.Dispose();
        foreach (var row in snapshot.Owned) row.Dispose();
    }

    /// <summary>What one <see cref="Clear"/> took, kept whole so undoing it restores the exchange the
    /// user reads and the message list the model sees together.</summary>
    private sealed record ClearedConversation(
        IReadOnlyList<AssistantRow> Owned,
        IReadOnlyList<AssistantToolGroup> Groups,
        IReadOnlyList<AssistantRow> Visible,
        IReadOnlyList<AssistantMessage> Conversation);

    // Appends a row, folding a run of adjacent tool calls into the row that opened it. Adjacency is
    // the whole rule: anything that is not a tool call ends the run, so a group never spans an answer.
    private void Add(AssistantRow row)
    {
        _owned.Add(row);
        _produced = true;

        if (row.Kind != AssistantRowKind.Tool)
        {
            _run = null;
            Rows.Add(row);
            return;
        }

        if (_run is not null)
        {
            _run.Add(row);
            return;
        }

        var group = new AssistantToolGroup();
        group.Add(row);
        _groups.Add(group);
        _run = group;
        row.Group = group;
        Rows.Add(row);
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        string? failure = null;
        try
        {
            await foreach (var e in _turnLoop.RunAsync(_turnMessages, AssistantAgentLoop.RepoStateBlock(_repo, _loc), _approvals, cts.Token).ConfigureAwait(false))
            {
                var captured = e;
                _dispatcher.Post(() => Apply(captured));
            }
        }
        catch (OperationCanceledException)
        {
            // A stopped turn is the user's own doing — nothing to report in the transcript.
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        _dispatcher.Post(() => Complete(cts, failure));
    }

    // Everything else the assistant needs about the repository comes from its tools; this is only
    // enough for it to know where it is without spending a turn asking.
    //
    // The reply language rides here rather than in the system prompt on purpose: the system block is
    // the cached prefix, so templating a user setting into it would re-bill the whole prefix on every
    // language change and make its byte-stability depend on a preference. This block is the uncached,
    // per-turn channel — volatile state is exactly what it is for. Read per turn, so switching
    // language takes effect on the next message.
    private void Apply(AssistantEvent e)
    {
        if (_disposed) return;

        switch (e)
        {
            case AssistantEvent.TextDelta delta:
                _thinking.Value = false;
                Reply().Append(delta.Text);
                _resolved = true;
                break;

            // Thinking display is omitted, so this only reports that the model started reasoning.
            case AssistantEvent.Thinking:
                _thinking.Value = true;
                break;

            case AssistantEvent.ToolStarted started:
                _thinking.Value = false;
                FinishReply();
                var row = AssistantRow.Tool(started.Name);
                _toolRows[started.Id] = row;
                Add(row);
                break;

            case AssistantEvent.ToolFinished finished:
                if (_toolRows.Remove(finished.Id, out var toolRow))
                    toolRow.Finish(finished.IsError);
                break;

            // The model answered without touching a tool where tool calling was never demonstrated,
            // so the answer was written from nothing but the prompt. Said in the transcript rather
            // than left for the reader to notice.
            case AssistantEvent.NoToolSupport:
                FinishReply();
                Add(AssistantRow.Notice(_loc.Strings.Value.AssistantNoToolSupport));
                break;

            // A turn can end having emitted nothing: a reasoning model that spent its whole budget
            // thinking, or an endpoint that stopped with an empty choice. Every other arm appends
            // something, so this is the only place a question would otherwise be followed by silence.
            case AssistantEvent.Completed completed:
                FinishReply();
                if (!_produced) Add(AssistantRow.Notice(EmptyTurnNotice(completed.Reason)));
                _resolved = true;
                break;

            case AssistantEvent.Refused refused:
                FinishReply();
                Add(AssistantRow.Refusal(refused.Explanation));
                _resolved = false;
                break;

            case AssistantEvent.Failed failed:
                FinishReply();
                Add(AssistantRow.Error(failed.Message));
                _resolved = false;
                break;
        }
    }

    // Running out of room is the one empty turn worth telling apart: it is the model's budget rather
    // than the model having nothing to say, and shortening the question is a way out of it. The rest
    // read the same to the person waiting, so they share a sentence.
    private string EmptyTurnNotice(StopReason reason) =>
        reason == StopReason.MaxTokens
            ? _loc.Strings.Value.AssistantEmptyReplyLength
            : _loc.Strings.Value.AssistantEmptyReply;

    // Raised on the UI thread by the approval queue while the turn is suspended on it. The reply so
    // far is closed off and the thinking pulse dropped: nothing more is coming until this is answered.
    private void Ask(PendingToolApproval pending)
    {
        if (_disposed)
        {
            pending.Cancel();
            return;
        }

        _thinking.Value = false;
        FinishReply();
        _pending = pending;
        Add(AssistantRow.Approval(pending));
    }

    private AssistantRow Reply()
    {
        if (_reply is not null) return _reply;
        _reply = AssistantRow.Reply();
        Add(_reply);
        return _reply;
    }

    private void FinishReply()
    {
        _reply?.Finish();
        _reply = null;
    }

    private void Complete(CancellationTokenSource cts, string? failure)
    {
        if (_disposed) return;

        if (failure is not null)
        {
            FinishReply();
            Add(AssistantRow.Error(failure));
            _resolved = false;
        }

        // A turn that never resolved is rolled back out of the model's view, so the next send isn't
        // a second user message stacked on a half-finished exchange — a partial answer, tool calls
        // whose results never came. A turn that ran to completion stays even when it said nothing:
        // the message list is whole, and dropping it would erase what was asked from the model's
        // side of a transcript that still shows the question.
        if (!_resolved && _turnMessages.Count > _turnStart)
            _turnMessages.RemoveRange(_turnStart, _turnMessages.Count - _turnStart);

        foreach (var running in _toolRows.Values)
            running.Finish(failed: true);
        _toolRows.Clear();

        // A question nobody answered dies with the turn, so its card stops offering buttons that
        // would resume a loop that is no longer there.
        _pending?.Cancel();
        _pending = null;

        FinishReply();
        _busy.Value = false;
        _thinking.Value = false;
        if (ReferenceEquals(_turn, cts)) _turn = null;
        cts.Dispose();

        // A clear asked for mid-turn waited for this: the stream is down, so the rows it was writing
        // into can go. A provider switch waited for the same thing, and takes only the messages.
        if (_clearPending)
        {
            _clearPending = false;
            ClearNow();
        }

        if (!_restartPending) return;
        _restartPending = false;
        RestartNow();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _turn?.Cancel();
        DiscardCleared();
        foreach (var group in _groups) group.Dispose();
        _groups.Clear();
        foreach (var row in _owned) row.Dispose();
        _owned.Clear();
        Rows.Clear();
        _busy.Dispose();
        _thinking.Dispose();
    }
}
