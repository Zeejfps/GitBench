using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Features.Diff.Reading;

/// <summary>What reading mode is doing right now, as the header renders it.</summary>
internal enum ReadingPhase
{
    /// <summary>No plan has been asked for yet.</summary>
    Idle,

    /// <summary>An abridgement is in flight.</summary>
    Working,

    /// <summary>A plan exists and the panes are drawing through it.</summary>
    Showing,

    /// <summary>A plan exists but the reader asked for the full diff back.</summary>
    Held,

    /// <summary>The last attempt produced nothing, and <see cref="ReadingModeCoordinator.Status"/>
    /// says why.</summary>
    Failed,
}

/// <summary>
/// Reading mode for one review surface: gathers the whole change, gets a plan for it, and hands the
/// same plan to every file pane.
/// </summary>
/// <remarks>
/// The plan is made against the whole change on purpose. A row that looks mechanical in one file is
/// often explained by a change in another, and a per-file pass cannot see that; it is also the
/// difference between one agent run per review and one per file.
///
/// Holding a plan and showing it are deliberately separate. Switching back to the full diff keeps
/// the plan in hand, so flipping between the two is instant in both directions rather than costing
/// a fresh round trip the second time — which is what makes it usable as a way of reading rather
/// than a one-way door.
///
/// Nothing here touches a <see cref="DiffResult"/>. The panes keep rendering the real diff and only
/// draw it differently, so staging, discarding and every other hunk action work in reading mode
/// exactly as they do outside it.
/// </remarks>
internal sealed class ReadingModeCoordinator : IDisposable
{
    private readonly IGitDiffReader _diffs;
    private readonly Repo _repo;
    private readonly ILocalizationService _loc;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<DiffAbridger?> _abridger;
    private readonly IReadable<bool> _available;

    private readonly State<ReadingPhase> _phase = new(ReadingPhase.Idle);
    private readonly State<ReadingOverlay?> _plan = new(null);
    private readonly State<ReadingOverlay?> _shown = new(null);
    private readonly State<string?> _status = new(null);
    private readonly State<string?> _activity = new(null);

    private CancellationTokenSource? _run;
    private bool _disposed;

    public ReadingModeCoordinator(
        Repo repo,
        IGitDiffReader diffs,
        ILocalizationService loc,
        IUiDispatcher dispatcher,
        Func<DiffAbridger?> abridger,
        IReadable<bool> available)
    {
        _available = available;
        _repo = repo;
        _diffs = diffs;
        _loc = loc;
        _dispatcher = dispatcher;
        _abridger = abridger;
    }

    /// <summary>
    /// Whether an abridgement could run at all right now.
    /// </summary>
    /// <remarks>Observable rather than a fixed answer because credentials resolve asynchronously
    /// at startup: a review window opened in the first moment would otherwise be stuck without the
    /// toggle for as long as it stayed open.</remarks>
    public IReadable<bool> Available => _available;

    public IReadable<ReadingPhase> Phase => _phase;

    /// <summary>The plan the panes draw through, or null whenever the full diff is on screen.</summary>
    public IReadable<ReadingOverlay?> Overlay => _shown;

    /// <summary>How much of the change is being hidden, or why nothing could be hidden. Null before
    /// the first run.</summary>
    public IReadable<string?> Status => _status;

    /// <summary>What the run is doing this moment — the file it is reading, or null between tool
    /// calls. Only meaningful while <see cref="Phase"/> is <see cref="ReadingPhase.Working"/>.</summary>
    public IReadable<string?> Activity => _activity;

    /// <summary>The model's one-line description of the change, once a plan exists.</summary>
    public string? Summary => _plan.Value?.Summary;

    /// <summary>Whether a plan is in hand, so switching views costs nothing.</summary>
    public bool HasPlan => _plan.Value != null;

    /// <summary>
    /// Flips between the abridged and the full diff, asking for a plan the first time.
    /// </summary>
    /// <remarks>Pressed while a run is in flight, this cancels it — the reader changed their mind,
    /// and a request they no longer want should not go on spending.</remarks>
    public void Toggle(IReadOnlyList<(string Path, string? CommitSha, string? BaseSha, DiffSide Side)> files)
    {
        switch (_phase.Value)
        {
            case ReadingPhase.Working:
                Cancel();
                break;
            case ReadingPhase.Showing:
                ShowFullDiff();
                break;
            default:
                ShowAbridged(files);
                break;
        }
    }

    /// <summary>Draws the abridged diff, running an abridgement first if there is no plan yet.</summary>
    public void ShowAbridged(IReadOnlyList<(string Path, string? CommitSha, string? BaseSha, DiffSide Side)> files)
    {
        if (_phase.Value == ReadingPhase.Working) return;
        if (_plan.Value is { } existing)
        {
            _shown.Value = existing;
            _phase.Value = ReadingPhase.Showing;
            return;
        }
        Start(files);
    }

    /// <summary>Puts the full diff back, keeping the plan for the next flip.</summary>
    public void ShowFullDiff()
    {
        if (_phase.Value == ReadingPhase.Working) Cancel();
        _shown.Value = null;
        _phase.Value = _plan.Value != null ? ReadingPhase.Held : ReadingPhase.Idle;
    }

    /// <summary>Abandons a run in flight, leaving whatever was on screen before it.</summary>
    public void Cancel()
    {
        _run?.Cancel();
        _run = null;
        _activity.Value = null;
        _status.Value = null;
        _phase.Value = _plan.Value != null ? ReadingPhase.Held : ReadingPhase.Idle;
    }

    /// <summary>Throws away the plan, so the next request re-reads the change. Used when the range
    /// underneath moves and the coordinates would no longer line up.</summary>
    public void Invalidate()
    {
        _run?.Cancel();
        _run = null;
        _plan.Value = null;
        _shown.Value = null;
        _status.Value = null;
        _activity.Value = null;
        _phase.Value = ReadingPhase.Idle;
    }

    private void Start(IReadOnlyList<(string Path, string? CommitSha, string? BaseSha, DiffSide Side)> files)
    {
        if (_abridger() is not { } abridger)
        {
            _phase.Value = ReadingPhase.Failed;
            _status.Value = _loc.Strings.Value.ReadingNoPlanProduced;
            return;
        }

        _phase.Value = ReadingPhase.Working;
        _status.Value = null;
        _activity.Value = null;

        var cts = new CancellationTokenSource();
        _run = cts;
        abridger.OnProgress = name => _dispatcher.Post(() =>
        {
            if (!_disposed && ReferenceEquals(_run, cts)) _activity.Value = name;
        });

        _ = Task.Run(async () =>
        {
            // Every pane loads its own diff lazily as it scrolls into view, but a plan has to see
            // the whole change at once — so the diffs are gathered here, off the UI thread, in the
            // list's own order so the numbering is stable across runs.
            var loaded = new List<DiffResult>(files.Count);
            foreach (var f in files)
            {
                cts.Token.ThrowIfCancellationRequested();
                loaded.Add(_diffs.GetDiff(_repo, f.Path, f.Side, f.CommitSha, f.BaseSha));
            }

            var (overlay, failure) = await abridger.AbridgeAsync(loaded, cts.Token).ConfigureAwait(false);
            _dispatcher.Post(() => Complete(cts, overlay, failure));
        }, cts.Token);
    }

    private void Complete(CancellationTokenSource cts, ReadingOverlay? overlay, ReadingFailure? failure)
    {
        if (_disposed || !ReferenceEquals(_run, cts)) return;
        _run = null;
        _activity.Value = null;

        if (overlay is null)
        {
            // A cancelled run reports nothing: the reader stopped it.
            if (failure is null) { Cancel(); return; }
            _phase.Value = ReadingPhase.Failed;
            _status.Value = failure.Message;
            return;
        }

        _plan.Value = overlay;
        _shown.Value = overlay;
        _phase.Value = ReadingPhase.Showing;
        var stats = overlay.Stats;
        _status.Value = _loc.Strings.Value.ReadingKept(
            stats.VisibleChanged, stats.RawChanged, stats.VisibleFiles, stats.RawFiles);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _run?.Cancel();
        _phase.Dispose();
        _plan.Dispose();
        _shown.Dispose();
        _status.Dispose();
        _activity.Dispose();
    }
}
