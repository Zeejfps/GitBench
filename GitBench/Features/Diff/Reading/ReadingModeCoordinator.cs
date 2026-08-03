using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Features.Diff.Reading;

/// <summary>What reading mode is doing right now, as the header renders it.</summary>
internal enum ReadingPhase { Off, Working, On, Failed }

/// <summary>
/// Reading mode for one review surface: gathers the whole change, gets a plan for it, and hands the
/// same plan to every file pane.
/// </summary>
/// <remarks>
/// The plan is made against the whole change on purpose. A row that looks mechanical in one file is
/// often explained by a change in another, and a per-file pass cannot see that; it is also the
/// difference between one agent run per review and one per file.
///
/// Nothing here touches a <see cref="DiffResult"/>. The panes keep rendering the real diff and only
/// draw it differently, so staging, discarding and every other hunk action work in reading mode
/// exactly as they do outside it — and the toggle back is instant, because the raw diff never went
/// anywhere.
/// </remarks>
internal sealed class ReadingModeCoordinator : IDisposable
{
    private readonly IGitDiffReader _diffs;
    private readonly Repo _repo;
    private readonly ILocalizationService _loc;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<DiffAbridger?> _abridger;
    private readonly IReadable<bool> _available;

    private readonly State<ReadingPhase> _phase = new(ReadingPhase.Off);
    private readonly State<ReadingOverlay?> _overlay = new(null);
    private readonly State<string?> _status = new(null);

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

    /// <summary>The plan every pane draws through, or null when reading mode is off.</summary>
    public IReadable<ReadingOverlay?> Overlay => _overlay;

    /// <summary>The line under the toggle: progress while working, the retention manifest once a
    /// plan is on, the reason when one could not be made.</summary>
    public IReadable<string?> Status => _status;

    /// <summary>The model's one-line description of the change, once a plan exists.</summary>
    public string? Summary => _overlay.Value?.Summary;

    /// <summary>Turns reading mode on for a set of files, or off if it already is on. A second
    /// request while a run is in flight cancels it rather than queueing behind it.</summary>
    public void Toggle(IReadOnlyList<(string Path, string? CommitSha, string? BaseSha, DiffSide Side)> files)
    {
        if (_phase.Value != ReadingPhase.Off)
        {
            TurnOff();
            return;
        }
        Start(files);
    }

    public void TurnOff()
    {
        _run?.Cancel();
        _run = null;
        _phase.Value = ReadingPhase.Off;
        _overlay.Value = null;
        _status.Value = null;
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
        _status.Value = _loc.Strings.Value.ReadingWorking;

        var cts = new CancellationTokenSource();
        _run = cts;
        abridger.OnProgress = name => _dispatcher.Post(() =>
        {
            if (!_disposed && ReferenceEquals(_run, cts)) _status.Value = name;
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

        if (overlay is null)
        {
            // A cancelled run reports nothing: the person turned it off.
            if (failure is null) { TurnOff(); return; }
            _phase.Value = ReadingPhase.Failed;
            _status.Value = failure.Message;
            return;
        }

        _overlay.Value = overlay;
        _phase.Value = ReadingPhase.On;
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
        _overlay.Dispose();
        _status.Dispose();
    }
}
