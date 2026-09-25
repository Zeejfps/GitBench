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
/// and the hand-off between the agent and the user. The agent proposes a stop and ends its turn; the
/// user edits and presses Next, and their diff since the stop was shown goes to the agent as its next
/// turn. Every member is UI-thread only.
/// </summary>
/// <remarks>
/// Nothing waits on the user: an agent with a stop open costs nothing until they move. The moves go
/// out in the order the user made them, so a Done is never lost behind a question asked after it.
/// One open stop ever: a stop proposed while another is open is refused unless it says it replaces it.
/// </remarks>
internal sealed class PairingStore : IDisposable
{
    private readonly IPairingPresentation _presentation;
    private readonly IPairingWorkspace _workspace;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<PairingAction> _deliver;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

    private readonly State<PairingPhase> _phase = new(new PairingPhase.Starting());
    private readonly State<IReadOnlyList<RoadmapEntry>> _roadmap = new(Array.Empty<RoadmapEntry>());
    private readonly State<OpenStop?> _stop = new(null);
    private readonly State<StopActivity> _activity = new(StopActivity.Idle);
    private readonly AgentTranscript _transcript;

    private IReadOnlyList<Milestone> _milestones = Array.Empty<Milestone>();
    private int _stopsSent;

    // An agent call is opening a stop across awaits; a second one meanwhile would work from the
    // state the first is about to replace.
    private bool _changingStop;
    private AgentTurn _turn = AgentTurn.Unseen;
    private bool _handedOver;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public PairingStore(
        string goal,
        string harness,
        AgentTranscript transcript,
        IPairingPresentation presentation,
        IPairingWorkspace workspace,
        IUiDispatcher dispatcher,
        Action<PairingAction> deliver)
    {
        Goal = goal;
        Harness = harness;
        _presentation = presentation;
        _workspace = workspace;
        _dispatcher = dispatcher;
        _deliver = deliver;
        _transcript = transcript;
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

    /// <summary>Whether the agent has left the user something to act on or read since its turn
    /// began: a stop, or a reply. A turn that ends without either left the user nothing.</summary>
    public bool HasHandedOver => _stop.Value is not null || _handedOver;

    // ── the agent's side ─────────────────────────────────────────────────────────────────────

    /// <summary>The agent is up and driving.</summary>
    public void MarkRunning()
    {
        if (_phase.Value is PairingPhase.Starting) _phase.Value = new PairingPhase.Running(false);
    }

    /// <summary>The agent's turn began. From here the user's turn is marked by the turn ending, not
    /// by what the agent sends along the way.</summary>
    public void MarkTurnStarted()
    {
        _turn = AgentTurn.Running;
        _handedOver = false;
        SetWaiting(false);
    }

    /// <summary>The agent's turn ended: whatever comes next is the user's.</summary>
    public void MarkTurnEnded()
    {
        _turn = AgentTurn.Over;
        SetWaiting(true);
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
                $"Stop {open.Stop.Number} (\"{open.Stop.Title}\") is still open. End your turn: the user's move on it "
                + "comes to you as your next message. Or pass replace: true to take it back and open this one instead.");
        if (_activity.Value != StopActivity.Idle)
            return new StopOpening.Refused("The user is finishing the open stop right now. End your turn: their move comes to you next.");
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
            return new StopOpening.Refused("The user moved on while the stop was being placed. End your turn: their move comes to you next.");
        }

        if (_stop.Value?.CreatedFile is { } previous && previous != created) TakeBack(previous);
        var stop = new PairingStop(++_stopsSent, target, title, reason);
        var opened = new OpenStop(stop, location, baseline, draft, new DraftState.Offered(), created);
        _stop.Value = opened;
        _presentation.RevealDraft(location, draft);
        _presentation.ShowDraft(location, draft, new SuggestionActions(() => _ = AcceptAsync(), () => _ = AcceptAndNextAsync()));
        _transcript.CloseNarration();
        HandOver();
        return new StopOpening.Opened(opened);
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
        if (_disposed) return;
        _transcript.AddReply(markdown);
        HandOver();
    }

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

    /// <summary>The user ends the session, and the agent is told.</summary>
    public void EndByUser()
    {
        if (!IsLive) return;
        var at = StopNumber;
        Finish(new PairingPhase.Ended(null));
        _deliver(new PairingAction.Ended(at));
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
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────

    private int StopNumber => _stop.Value?.Stop.Number ?? 0;

    private void Finish(PairingPhase phase)
    {
        TakeBack(_stop.Value?.CreatedFile);
        _stop.Value = null;
        _presentation.ClearDraft();
        _activity.Value = StopActivity.Idle;
        _transcript.CloseNarration();
        _phase.Value = phase;
    }

    // A file a stop created for the user to fill goes again if they moved on and left it empty.
    private void TakeBack(string? createdFile)
    {
        if (createdFile is not null) _ = _workspace.RemoveIfEmptyAsync(createdFile, CancellationToken.None);
    }

    private void Deliver(PairingAction action)
    {
        SetWaiting(false);
        _deliver(action);
    }

    // The agent gave the user something. Where its turns are seen, the turn ending is what hands
    // over; an agent in a terminal says nothing of its turns, so this is all there is to go on.
    private void HandOver()
    {
        _handedOver = true;
        if (_turn != AgentTurn.Running) SetWaiting(true);
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

    private enum AgentTurn
    {
        /// <summary>No turn has been reported: the agent runs where the app can't see its turns.</summary>
        Unseen,
        Running,
        Over,
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
