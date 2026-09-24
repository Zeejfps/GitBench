using System.Runtime.CompilerServices;
using GitBench.Features.Assistant;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>What the loop needs from the editor. UI thread only.</summary>
internal interface IPairingPresentation
{
    /// <summary>Finds the stop's declaration in the file as it is now, unsaved edits included.
    /// Moves nothing.</summary>
    Task<StopPlacement> LocateAsync(StopTarget target, CancellationToken ct);

    /// <summary>Draws the agent's code for the stop into its file where it goes, with Accept and
    /// Accept &amp; next on it.</summary>
    void ShowDraft(StopLocation location, StopDraft draft, SuggestionActions actions);

    /// <summary>The stop's file as the user has it now, unsaved edits included; null when it can't
    /// be read.</summary>
    Task<string?> ReadTextAsync(StopLocation location);

    /// <summary>Takes the agent's code off the editor.</summary>
    void ClearDraft();

    /// <summary>Puts the agent's code into the stop's open file as one undo step, unsaved. Answers
    /// whether it went in.</summary>
    Task<bool> TakeDraftAsync(StopLocation location);

    /// <summary>Puts the caret on a place a stop or <c>pairing_show</c> found.</summary>
    void Reveal(StopLocation location);

    /// <summary>Puts the caret where the agent's code for a stop goes: the first line it replaces,
    /// or the end of the line it goes in after.</summary>
    void RevealDraft(StopLocation location, StopDraft draft);

    /// <summary>Opens a repository file with the caret at the start of a line; false when there is
    /// no such file.</summary>
    bool ShowFile(string relativePath, int line);

    /// <summary>Where the user's caret is, and what they have selected.</summary>
    IReadable<EditorCaret?> Caret { get; }

    /// <summary>The names the agent's code for the open stop uses that nothing declares yet, each
    /// once; empty until a language server has said, and where none has.</summary>
    IReadable<IReadOnlyList<string>> DraftNeeds { get; }

    /// <summary>Writes every unsaved file of the repository; answers the ones that failed.</summary>
    IReadOnlyList<string> SaveUnsaved();

    /// <summary>The repository-relative form of an absolute path, or null outside it.</summary>
    string? Relative(string absolutePath);
}

/// <summary>What the loop needs from the working tree. Every member may be called from the UI
/// thread; the work happens off it.</summary>
internal interface IPairingWorkspace
{
    Task<SnapshotResult<TreeSnapshot>> CaptureAsync(CancellationToken ct);

    Task<SnapshotResult<string>> DiffAsync(TreeSnapshot from, TreeSnapshot to, CancellationToken ct);

    /// <summary>Makes sure a new file's stop has a file to open: creates it empty, or finds it
    /// already there with nothing in it. Refused when it has content.</summary>
    Task<FileCreation> EnsureEmptyFileAsync(string relativePath, CancellationToken ct);

    /// <summary>Deletes a file a stop created, unless something was written into it.</summary>
    Task RemoveIfEmptyAsync(string relativePath, CancellationToken ct);
}

/// <summary>How making sure of an empty file went.</summary>
internal abstract record FileCreation
{
    public sealed record Created : FileCreation;

    public sealed record AlreadyEmpty : FileCreation;

    public sealed record Refused(string Reason) : FileCreation;
}

/// <summary>How a request to open a stop came out.</summary>
internal abstract record StopOpening
{
    public sealed record Opened(OpenStop Stop) : StopOpening;

    public sealed record Refused(string Message) : StopOpening;
}

/// <summary>How a request to show the user a place came out.</summary>
internal abstract record Showing
{
    public sealed record Shown(int Line, string? LineText) : Showing;

    public sealed record Refused(string Message) : Showing;
}

/// <summary>
/// One pairing session's loop: the goal, the roadmap, the stop the user is on, its part of the conversation,
/// and the hand-off between the agent and the user. The agent proposes a stop and waits; the user
/// edits and presses Done, and the wait completes with their diff since the stop was shown. Every
/// member is UI-thread only; the wait's task is what crosses to a tool thread.
/// </summary>
/// <remarks>
/// One waiter ever: a newer wait, an end or a disconnect cancels the one attached. The user's moves
/// queue while no waiter is attached and the next wait takes the oldest, so a Done is never lost
/// behind a question asked after it. One open stop ever: a stop proposed while another is open is
/// refused unless it says it replaces it.
/// </remarks>
internal sealed class PairingStore : IDisposable
{
    /// <summary>How long one wait blocks before answering <see cref="PairingAction.Pending"/>, under
    /// the per-call timeout MCP clients enforce.</summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(45);

    private readonly IPairingPresentation _presentation;
    private readonly IPairingWorkspace _workspace;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

    private readonly State<PairingPhase> _phase = new(new PairingPhase.Starting());
    private readonly State<IReadOnlyList<RoadmapEntry>> _roadmap = new(Array.Empty<RoadmapEntry>());
    private readonly State<OpenStop?> _stop = new(null);
    private readonly State<StopActivity> _activity = new(StopActivity.Idle);
    private readonly AgentTranscript _transcript;
    private readonly int _transcriptStart;
    private readonly Queue<PairingAction> _queued = new();

    private IReadOnlyList<Milestone> _milestones = Array.Empty<Milestone>();
    private int _stopsSent;

    // An agent call is opening a stop across awaits; a second one meanwhile would work from the
    // state the first is about to replace.
    private bool _changingStop;
    private Waiter? _waiter;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public PairingStore(
        string goal,
        string harness,
        AgentTranscript transcript,
        IPairingPresentation presentation,
        IPairingWorkspace workspace,
        IUiDispatcher dispatcher,
        TimeProvider clock)
    {
        Goal = goal;
        Harness = harness;
        _presentation = presentation;
        _workspace = workspace;
        _dispatcher = dispatcher;
        _clock = clock;
        _transcript = transcript;
        _transcriptStart = transcript.Count;
    }

    public string Goal { get; }

    /// <summary>Who drives the session, as the panel names it.</summary>
    public string Harness { get; }

    public IReadable<PairingPhase> Phase => _phase;

    /// <summary>The agent's current roadmap, with what its last revision added or dropped.</summary>
    public IReadable<IReadOnlyList<RoadmapEntry>> Roadmap => _roadmap;

    /// <summary>The stop the user is on, or null between stops.</summary>
    public IReadable<OpenStop?> Stop => _stop;

    /// <summary>What Done is busy with.</summary>
    public IReadable<StopActivity> Activity => _activity;

    /// <summary>Where the user is in the editor.</summary>
    public IReadable<EditorCaret?> Caret => _presentation.Caret;

    public IReadable<IReadOnlyList<string>> DraftNeeds => _presentation.DraftNeeds;

    /// <summary>The repository-relative form of an absolute path, or null outside it. Any thread.</summary>
    public string? Relative(string absolutePath) => _presentation.Relative(absolutePath);

    /// <summary>Whether the session can still take moves from either side.</summary>
    public bool IsLive => _phase.Value is PairingPhase.Starting or PairingPhase.Running;

    // ── the agent's side ─────────────────────────────────────────────────────────────────────

    /// <summary>The agent is up and driving.</summary>
    public void MarkRunning()
    {
        if (_phase.Value is PairingPhase.Starting) _phase.Value = new PairingPhase.Running(_waiter is not null);
    }

    /// <summary>Replaces the roadmap, marking what the revision added and what it dropped.</summary>
    public void SetRoadmap(IReadOnlyList<Milestone> milestones)
    {
        ThrowIfDisposed();
        _roadmap.Value = RoadmapDiff.Compare(_milestones, milestones);
        _milestones = milestones;
    }

    /// <summary>Opens a stop: places it and the agent's code for it in the editor, and captures the
    /// working tree it starts from. Refused while another stop is open, unless <paramref name="replace"/>.</summary>
    public async Task<StopOpening> OpenStopAsync(
        StopTarget target, string title, string reason, DraftRequest draft, bool replace, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!IsLive) return new StopOpening.Refused("The session has ended.");
        if (_stop.Value is { } open && !replace)
            return new StopOpening.Refused(
                $"Stop {open.Stop.Number} (\"{open.Stop.Title}\") is still open. Call pairing_wait for the user's Done, "
                + "or pass replace: true to take it back and open this one instead.");
        if (_activity.Value != StopActivity.Idle)
            return new StopOpening.Refused("The user is finishing the open stop right now. Call pairing_wait.");
        if (_changingStop) return new StopOpening.Refused("Another pairing call is still changing the stop. Wait for it to return.");

        _changingStop = true;
        try
        {
            return await OpenStopLockedAsync(target, title, reason, draft, replace, ct);
        }
        finally
        {
            await OnUi();
            _changingStop = false;
        }
    }

    private async Task<StopOpening> OpenStopLockedAsync(
        StopTarget target, string title, string reason, DraftRequest request, bool replace, CancellationToken ct)
    {
        StopLocation location;
        switch (await _presentation.LocateAsync(target, ct))
        {
            case StopPlacement.Placed placed:
                location = placed.Location;
                break;
            case StopPlacement.Missed missed:
                return new StopOpening.Refused(Describe(missed.Miss));
            default:
                throw new InvalidOperationException("Unhandled stop placement.");
        }

        StopDraft draft;
        switch (DraftPlacing.Place(location, request))
        {
            case DraftPlacement.Placed placed:
                draft = placed.Draft;
                break;
            case DraftPlacement.Refused refused:
                await OnUi();
                return new StopOpening.Refused(refused.Message);
            default:
                throw new InvalidOperationException("Unhandled draft placement.");
        }

        await OnUi();
        string? created = null;
        if (location is StopLocation.NewFile)
        {
            var ensured = await _workspace.EnsureEmptyFileAsync(target.Path, ct);
            await OnUi();
            switch (ensured)
            {
                case FileCreation.Created:
                    created = target.Path;
                    break;
                case FileCreation.AlreadyEmpty:
                    created = _stop.Value?.CreatedFile == target.Path ? target.Path : null;
                    break;
                case FileCreation.Refused refused:
                    return new StopOpening.Refused(refused.Reason);
                default:
                    throw new InvalidOperationException("Unhandled file creation.");
            }
        }

        TreeSnapshot baseline;
        switch (await _workspace.CaptureAsync(ct))
        {
            case SnapshotResult<TreeSnapshot>.Ok ok:
                baseline = ok.Value;
                break;
            case SnapshotResult<TreeSnapshot>.Failed failed:
                await OnUi();
                TakeBack(created);
                return new StopOpening.Refused($"The working tree could not be read: {failed.Reason}");
            default:
                throw new InvalidOperationException("Unhandled snapshot result.");
        }

        await OnUi();
        if (!IsLive)
        {
            TakeBack(created);
            return new StopOpening.Refused("The session has ended.");
        }

        if (_activity.Value != StopActivity.Idle || (_stop.Value is not null && !replace))
        {
            TakeBack(created);
            return new StopOpening.Refused("The user moved on while the stop was being placed. Call pairing_wait.");
        }

        if (_stop.Value?.CreatedFile is { } previous && previous != created) TakeBack(previous);
        var stop = new PairingStop(++_stopsSent, target, title, reason);
        var opened = new OpenStop(stop, location, baseline, draft, new DraftState.Offered(), created);
        _stop.Value = opened;
        _presentation.RevealDraft(location, draft);
        _presentation.ShowDraft(location, draft, new SuggestionActions(() => _ = AcceptAsync(), () => _ = AcceptAndNextAsync()));
        _transcript.CloseNarration();
        return new StopOpening.Opened(opened);
    }

    /// <summary>Blocks until the user moves, up to <see cref="WaitTimeout"/>. A queued move answers
    /// at once. Attaching while another wait is attached cancels that one.</summary>
    public Task<PairingAction> WaitAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_queued.TryDequeue(out var queued)) return Task.FromResult(queued);
        if (!IsLive) return Task.FromResult<PairingAction>(new PairingAction.Ended(StopNumber));
        if (ct.IsCancellationRequested) return Task.FromResult<PairingAction>(new PairingAction.Cancelled(StopNumber));

        ResolveWaiter(new PairingAction.Cancelled(StopNumber));
        var waiter = new Waiter(StopNumber);
        _waiter = waiter;
        SetWaiting(true);
        _transcript.CloseNarration();
        waiter.Timer = _clock.CreateTimer(
            _ => _dispatcher.Post(() => OnTimeout(waiter)), null, WaitTimeout, Timeout.InfiniteTimeSpan);
        waiter.Registration = ct.Register(() =>
        {
            waiter.Completion.TrySetResult(new PairingAction.Cancelled(waiter.AttachedAt));
            _dispatcher.Post(() => Detach(waiter));
        });
        return waiter.Completion.Task;
    }

    /// <summary>The agent finished: the stop closes, the summary stays up.</summary>
    public void End(string? summary)
    {
        if (!IsLive) return;
        Finish(new PairingPhase.Ended(string.IsNullOrWhiteSpace(summary) ? null : summary));
    }

    /// <summary>The agent could not be started, or died.</summary>
    public void Fail(string reason)
    {
        if (!IsLive) return;
        Finish(new PairingPhase.Failed(reason));
    }

    /// <summary>The agent went quiet without ending; the user can only close the session.</summary>
    public void MarkDisconnected(string reason)
    {
        if (!IsLive) return;
        Finish(new PairingPhase.Disconnected(reason));
    }

    /// <summary>A whole reply from the agent, said through <c>pairing_say</c>.</summary>
    public void AddReply(string markdown)
    {
        if (!_disposed) _transcript.AddReply(markdown);
    }

    // Moving on from a stop starts the session's part of the conversation afresh: what was said
    // about one stop is noise under the next. The agent keeps its own memory of it.
    private void ClearConversation() => _transcript.ClearFrom(_transcriptStart);

    public void AddNotice(string text, NoticeTone tone)
    {
        if (!_disposed) _transcript.AddNotice(text, tone);
    }

    // ── the user's side ──────────────────────────────────────────────────────────────────────

    /// <summary>Finishes the open stop: saves what was typed and hands the agent the diff since
    /// the stop was shown.</summary>
    public async Task DoneAsync()
    {
        if (_disposed || _stop.Value is not { } open || _activity.Value != StopActivity.Idle) return;
        _activity.Value = StopActivity.Checking;
        await CloseAsync(open.Stop.Number);
    }

    /// <summary>Puts the agent's code for the open stop into the file as one undo step, and stays on
    /// the stop: the user may still change it before Next. Answers whether the code is in.</summary>
    public async Task<bool> AcceptAsync()
    {
        if (_disposed || _stop.Value is not { } open || _activity.Value != StopActivity.Idle) return false;
        if (open.DraftState is DraftState.Taken) return true;

        _activity.Value = StopActivity.Accepting;
        _presentation.RevealDraft(open.Location, open.Draft);
        var taken = await _presentation.TakeDraftAsync(open.Location);
        await OnUi();
        var text = taken ? await _presentation.ReadTextAsync(open.Location) : null;
        await OnUi();

        _activity.Value = StopActivity.Idle;
        if (!StillOn(open.Stop.Number)) return false;
        if (!taken)
        {
            AddNotice($"The agent's code could not be put into {open.Stop.Target.Path}. Open the file and accept again.", NoticeTone.Error);
            return false;
        }

        _stop.Value = _stop.Value! with { DraftState = new DraftState.Taken(text) };
        _presentation.ClearDraft();
        return true;
    }

    /// <summary>Accepts the agent's code and goes straight on, as Next does.</summary>
    public async Task AcceptAndNextAsync()
    {
        if (await AcceptAsync()) await DoneAsync();
    }

    // Saves, takes the diff since the stop's baseline, and hands it over as Done.
    private async Task CloseAsync(int number)
    {
        var problems = new List<string>(_presentation.SaveUnsaved());
        var baseline = _stop.Value!.Baseline;

        var diff = string.Empty;
        var after = await _workspace.CaptureAsync(_lifetime.Token);
        await OnUi();
        if (!StillOn(number))
        {
            _activity.Value = StopActivity.Idle;
            return;
        }
        switch (after)
        {
            case SnapshotResult<TreeSnapshot>.Ok ok:
                var compared = await _workspace.DiffAsync(baseline, ok.Value, _lifetime.Token);
                await OnUi();
                if (!StillOn(number))
                {
                    _activity.Value = StopActivity.Idle;
                    return;
                }
                switch (compared)
                {
                    case SnapshotResult<string>.Ok text:
                        diff = text.Value;
                        break;
                    case SnapshotResult<string>.Failed failed:
                        problems.Add($"The diff could not be taken: {failed.Reason}");
                        break;
                    default:
                        throw new InvalidOperationException("Unhandled diff result.");
                }

                break;
            case SnapshotResult<TreeSnapshot>.Failed failed:
                problems.Add($"The working tree could not be read: {failed.Reason}");
                break;
            default:
                throw new InvalidOperationException("Unhandled snapshot result.");
        }

        var outcome = await OutcomeAsync(_stop.Value!);
        await OnUi();
        if (!StillOn(number))
        {
            _activity.Value = StopActivity.Idle;
            return;
        }

        _activity.Value = StopActivity.Idle;
        _stop.Value = null;
        _presentation.ClearDraft();
        ClearConversation();
        Deliver(new PairingAction.Done(number, diff, outcome, problems));
    }

    // Accepted code the user changed afterwards is worth the agent's attention: the diff shows how
    // they would rather it read.
    private async Task<DraftOutcome> OutcomeAsync(OpenStop open)
    {
        if (open.DraftState is not DraftState.Taken taken) return DraftOutcome.NotAccepted;
        if (taken.FileText is null) return DraftOutcome.AcceptedAsIs;
        var now = await _presentation.ReadTextAsync(open.Location);
        return now is null || now == taken.FileText ? DraftOutcome.AcceptedAsIs : DraftOutcome.AcceptedThenEdited;
    }

    private bool StillOn(int number) => !_disposed && _stop.Value?.Stop.Number == number;

    /// <summary>Passes on the open stop without the diff.</summary>
    public void Skip()
    {
        if (_disposed || _stop.Value is not { } open || _activity.Value != StopActivity.Idle) return;
        _stop.Value = null;
        _presentation.ClearDraft();
        TakeBack(open.CreatedFile);
        ClearConversation();
        Deliver(new PairingAction.Skipped(open.Stop.Number));
    }

    /// <summary>Says something to the agent, with code the user sent along. It joins the
    /// conversation the agent keeps for the session, so "I went with an event instead" said here is
    /// in its mind when it reads the diff Done hands it next.</summary>
    public void Say(string text, CodeQuote? quote = null)
    {
        if (_disposed || !IsLive || string.IsNullOrWhiteSpace(text)) return;
        var said = text.Trim();
        _transcript.AddFromUser(said, quote);
        var told = quote is null ? said : said + "\n\n" + quote.ToMarkdown(path => Relative(path) ?? path);
        Deliver(new PairingAction.Message(StopNumber, told, _presentation.Caret.Value));
    }

    /// <summary>The user ends the session. The agent's wait answers <c>ended</c>.</summary>
    public void EndByUser()
    {
        if (!IsLive) return;
        var at = StopNumber;
        _queued.Clear();
        Finish(new PairingPhase.Ended(null), new PairingAction.Ended(at));
    }

    /// <summary>Takes the user to a place the agent points at while they talk: a declaration, or a
    /// line. The open stop, its card and its draft stay as they are; the card brings the user back.</summary>
    public async Task<Showing> ShowAsync(string path, string? symbol, int? line, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!IsLive) return new Showing.Refused("The session has ended.");
        if (symbol is not { Length: > 0 })
        {
            var at = Math.Max(1, line ?? 1);
            return _presentation.ShowFile(path, at)
                ? new Showing.Shown(at, null)
                : new Showing.Refused($"{path} is not a file in the repository.");
        }

        var placement = await _presentation.LocateAsync(new StopTarget(path, symbol, null), ct);
        await OnUi();
        if (!IsLive) return new Showing.Refused("The session has ended.");
        switch (placement)
        {
            case StopPlacement.Placed { Location: StopLocation.OnSymbol found }:
                _presentation.Reveal(found);
                return new Showing.Shown(found.At.Line.Value, found.LineText);
            case StopPlacement.Placed { Location: StopLocation.NewFile }:
                return new Showing.Refused($"{path} does not exist.");
            case StopPlacement.Placed { Location: StopLocation.Insertion }:
                throw new InvalidOperationException("A place to show has no insertion point.");
            case StopPlacement.Missed missed:
                return new Showing.Refused(Describe(missed.Miss));
            default:
                throw new InvalidOperationException("Unhandled placement.");
        }
    }

    /// <summary>Puts the caret back where the open stop's code goes.</summary>
    public void RevealStop()
    {
        if (_stop.Value is { } open) _presentation.RevealDraft(open.Location, open.Draft);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        ResolveWaiter(new PairingAction.Cancelled(StopNumber));
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────

    private int StopNumber => _stop.Value?.Stop.Number ?? 0;

    private void Finish(PairingPhase phase, PairingAction? lastWord = null)
    {
        TakeBack(_stop.Value?.CreatedFile);
        _stop.Value = null;
        _presentation.ClearDraft();
        _activity.Value = StopActivity.Idle;
        _transcript.CloseNarration();
        _phase.Value = phase;
        ResolveWaiter(lastWord ?? new PairingAction.Cancelled(StopNumber));
    }

    // A file a stop created for the user to fill goes again if they moved on and left it empty.
    private void TakeBack(string? createdFile)
    {
        if (createdFile is not null) _ = _workspace.RemoveIfEmptyAsync(createdFile, CancellationToken.None);
    }

    private void Deliver(PairingAction action)
    {
        if (_waiter is not null) ResolveWaiter(action);
        else _queued.Enqueue(action);
    }

    private void ResolveWaiter(PairingAction action)
    {
        if (_waiter is not { } waiter) return;
        Detach(waiter);
        // The caller's cancellation may have answered the wait on its own thread a moment ago; a
        // move of the user's that finds it answered waits for the next one instead.
        if (!waiter.Completion.TrySetResult(action) && IsMove(action)) _queued.Enqueue(action);
    }

    private static bool IsMove(PairingAction action) => action switch
    {
        PairingAction.Done or PairingAction.Message or PairingAction.Skipped => true,
        PairingAction.Ended or PairingAction.Pending or PairingAction.Cancelled => false,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action."),
    };

    private void OnTimeout(Waiter waiter)
    {
        if (_waiter != waiter) return;
        Detach(waiter);
        waiter.Completion.TrySetResult(new PairingAction.Pending(StopNumber));
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
        if (_phase.Value is PairingPhase.Running running && running.Waiting != waiting)
            _phase.Value = running with { Waiting = waiting };
    }

    private static string Describe(StopMiss miss) => miss switch
    {
        StopMiss.NoSuchSymbol none =>
            $"'{none.Symbol}' is not declared in {none.Path}. Name a declaration that exists, or pass 'after' with the "
            + $"declaration the new one goes after. Declared there: {Listed(none.Known)}",
        StopMiss.NoSuchAfter after =>
            $"'{after.After}' (the declaration to go after) is not declared in {after.Path}. Declared there: {Listed(after.Known)}",
        StopMiss.OutsideRepository outside => $"{outside.Path} is outside the repository.",
        StopMiss.Unreadable unreadable => $"{unreadable.Path} could not be read: {unreadable.Reason}",
        _ => throw new ArgumentOutOfRangeException(nameof(miss), miss, "Unknown miss."),
    };

    private static string Listed(IReadOnlyList<string> known) => known.Count == 0 ? "(nothing found)" : string.Join(", ", known);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PairingStore));
    }

    // Brings an async flow back to the UI thread; nothing here installs a synchronization context,
    // so an await on off-thread work resumes wherever that work finished.
    private UiResume OnUi() => new(_dispatcher, _uiThreadId);

    private readonly struct UiResume(IUiDispatcher dispatcher, int uiThreadId) : INotifyCompletion
    {
        public UiResume GetAwaiter() => this;

        public bool IsCompleted => Environment.CurrentManagedThreadId == uiThreadId;

        public void OnCompleted(Action continuation) => dispatcher.Post(continuation);

        public void GetResult() { }
    }

    private sealed class Waiter(int attachedAt)
    {
        public int AttachedAt { get; } = attachedAt;

        public TaskCompletionSource<PairingAction> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ITimer? Timer { get; set; }

        public CancellationTokenRegistration Registration { get; set; }
    }
}

/// <summary>A roadmap revision against the one before it.</summary>
internal static class RoadmapDiff
{
    public static IReadOnlyList<RoadmapEntry> Compare(IReadOnlyList<Milestone> before, IReadOnlyList<Milestone> after)
    {
        var entries = new List<RoadmapEntry>(after.Count + before.Count);
        var first = before.Count == 0;
        foreach (var milestone in after)
            entries.Add(new RoadmapEntry(milestone.Title, milestone.Done,
                first || Contains(before, milestone.Title) ? RoadmapChange.Kept : RoadmapChange.Added));
        foreach (var dropped in before)
            if (!dropped.Done && !Contains(after, dropped.Title))
                entries.Add(new RoadmapEntry(dropped.Title, false, RoadmapChange.Removed));
        return entries;
    }

    private static bool Contains(IReadOnlyList<Milestone> milestones, string title)
    {
        foreach (var milestone in milestones)
            if (string.Equals(milestone.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
