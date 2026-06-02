using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

public record DiffTarget(string Path, DiffSide Side, string? CommitSha = null);

internal abstract record DiffRenderState
{
    public sealed record Placeholder(string Text) : DiffRenderState;
    // Highlight is null until the async syntax pass completes (or stays null when highlighting
    // is off/unsupported/failed) — the view renders plain in that case, identical to before.
    public sealed record Loaded(DiffResult Result, DiffHighlight? Highlight = null) : DiffRenderState;
}

// Badge shown in the diff header for binary files: whether the blob lives in Git LFS or is
// stored inline. Text diffs and placeholders produce None, which hides the badge.
internal enum LfsBadge { None, Tracked, NotTracked }

internal sealed record DiffState(DiffRenderState Render, string? OpError);

internal sealed class DiffViewModel : ViewModelBase<DiffState>
{
    private const string EmptyPlaceholder = "Select a file to view diff.";
    private const string LoadingPlaceholder = "Loading…";

    private readonly IReadable<DiffTarget?> _target;
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private bool _deferReloadToWorkingTreeChange;

    // Syntax highlighting runs on its own lane so an in-flight highlight for a file we've
    // navigated away from is dropped, and so it never invalidates the diff-load lane (Gen).
    private readonly GenerationGuard _highlightLane;

    public IReadable<DiffRenderState> RenderState { get; }
    public IReadable<string?> OpError { get; }
    public IReadable<LfsBadge> LfsStatus { get; }

    // The side of the currently-loaded diff (null until a diff loads). Drives the header's
    // file-level Stage/Unstage button: Unstaged → "Stage file", Staged → "Unstage file",
    // Commit → hidden (history diffs aren't stageable).
    public IReadable<DiffSide?> CurrentSide { get; }

    public DiffViewModel(
        IReadable<DiffTarget?> target,
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
        : base(dispatcher, new DiffState(new DiffRenderState.Placeholder(EmptyPlaceholder), null))
    {
        _target = target;
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _highlightLane = CreateLane();

        RenderState = Slice(s => s.Render);
        OpError = Slice(s => s.OpError);
        LfsStatus = Slice(s => s.Render is DiffRenderState.Loaded { Result.IsBinary: true } loaded
            ? (loaded.Result.IsLfs ? LfsBadge.Tracked : LfsBadge.NotTracked)
            : LfsBadge.None);
        CurrentSide = Slice(s => s.Render is DiffRenderState.Loaded l ? l.Result.Side : (DiffSide?)null);

        Subscriptions.Add(_target.Subscribe(_ =>
        {
            if (_deferReloadToWorkingTreeChange) return;
            StartLoad();
        }));
        Subscriptions.Add(_bus.SubscribeScoped<WorkingTreeChangedMessage>(OnWorkingTreeChanged));
    }

    public void DeferReloadToWorkingTreeChange() => _deferReloadToWorkingTreeChange = true;

    private void OnWorkingTreeChanged(WorkingTreeChangedMessage msg)
    {
        var active = _registry.Active.Value;
        if (active == null || active.Id != msg.RepoId) return;
        _deferReloadToWorkingTreeChange = false;
        if (_target.Value == null) return;
        StartLoad();
    }

    public void StageHunk(int hunkIndex) => ApplyHunk(hunkIndex, cached: true, reverse: false);

    public void UnstageHunk(int hunkIndex) => ApplyHunk(hunkIndex, cached: true, reverse: true);

    // Pops the current diff into its own top-level window. DiffWindowPresenter handles the
    // message and spins up an independent, live DiffViewModel pinned to this target.
    public void RequestOpenInWindow()
    {
        var target = _target.Value;
        if (target == null) return;
        _bus.Broadcast(new OpenDiffWindowMessage(target));
    }

    public void StageFile() => RunFileIndexOp(stage: true);

    public void UnstageFile() => RunFileIndexOp(stage: false);

    // Stages/unstages the whole current file. Mirrors ApplyHunk's pattern: run the git op
    // off the UI thread, then broadcast a working-tree change so every list (and this diff)
    // re-syncs against the truth — LocalChangesViewModel owns the optimistic list updates.
    private void RunFileIndexOp(bool stage)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var target = _target.Value;
        if (target == null) return;

        var path = target.Path;
        var service = _gitService;
        var bus = _bus;
        var dispatcher = Dispatcher;
        var repoId = repo.Id;
        Task.Run(() =>
        {
            string? error = null;
            try
            {
                if (stage) service.Stage(repo, new[] { path });
                else service.Unstage(repo, new[] { path });
            }
            catch (Exception ex) { error = ex.Message; }

            dispatcher.Post(() =>
            {
                if (error != null)
                {
                    Update(s => s with { OpError = error });
                    return;
                }
                Update(s => s with { OpError = null });
                bus.Broadcast(new WorkingTreeChangedMessage(repoId));
            });
        });
    }

    public void RequestDiscardHunk(int hunkIndex)
    {
        if (!TryGetPatchContext(hunkIndex, out var repo, out var diff)) return;
        var patch = HunkPatchBuilder.Build(diff, hunkIndex);
        _bus.Broadcast(new ShowDialogMessage(onClose => new DiscardHunkDialog(repo, diff.Path, patch, onClose)));
    }

    private void ApplyHunk(int hunkIndex, bool cached, bool reverse)
    {
        if (!TryGetPatchContext(hunkIndex, out var repo, out var diff)) return;
        var patch = HunkPatchBuilder.Build(diff, hunkIndex);

        var isLastHunk = diff.Hunks.Count == 1;
        var fromSide = diff.Side;
        DiffSide? toSide = (cached, reverse) switch
        {
            (true, false) => DiffSide.Staged,    // stage: unstaged → staged
            (true, true) => DiffSide.Unstaged,   // unstage: staged → unstaged
            _ => null,
        };

        // Optimistic diff update — when there are hunks left, drop the just-applied one so
        // the diff repaints immediately. When this was the last hunk and the file moves to
        // another side, show a brief Loading placeholder; the selection swap below will
        // kick off a fresh load for the destination side and replace it.
        if (!isLastHunk)
        {
            var remainingHunks = new List<DiffHunk>(diff.Hunks.Count - 1);
            for (var i = 0; i < diff.Hunks.Count; i++)
                if (i != hunkIndex) remainingHunks.Add(diff.Hunks[i]);
            // Whole-file line numbering is unchanged by dropping a hunk, so the existing spans
            // stay valid — carry them through to avoid a highlight flicker before the reload.
            var highlight = CurrentHighlight();
            Update(s => s with { Render = new DiffRenderState.Loaded(diff with { Hunks = remainingHunks }, highlight) });
        }
        else if (toSide.HasValue)
        {
            Update(s => s with { Render = new DiffRenderState.Placeholder(LoadingPlaceholder) });
        }

        _bus.Broadcast(new HunkAppliedOptimisticMessage(repo.Id, diff.Path, fromSide, toSide, isLastHunk));

        // Intentionally unguarded: every apply must broadcast a working-tree change so the
        // optimistic move (here and in LocalChangesViewModel) reconciles against the truth,
        // so this op does not run through RunBackground's staleness drop.
        var service = _gitService;
        var bus = _bus;
        var dispatcher = Dispatcher;
        var repoId = repo.Id;
        var original = diff;
        Task.Run(() =>
        {
            string? error;
            try { error = service.ApplyPatch(repo, patch, cached, reverse); }
            catch (Exception ex) { error = ex.Message; }

            dispatcher.Post(() =>
            {
                if (error != null)
                {
                    Update(s => s with { OpError = error });
                    // Roll back the optimistic diff state, and broadcast a working-tree
                    // change so LocalChangesViewModel re-syncs its lists against the truth
                    // (we may have optimistically moved the file in OnHunkAppliedOptimistic).
                    if (State.Value.Render is DiffRenderState.Loaded)
                        Update(s => s with { Render = new DiffRenderState.Loaded(original, CurrentHighlight()) });
                    bus.Broadcast(new WorkingTreeChangedMessage(repoId));
                    return;
                }
                Update(s => s with { OpError = null });
                bus.Broadcast(new WorkingTreeChangedMessage(repoId));
            });
        });
    }

    private DiffHighlight? CurrentHighlight()
        => State.Value.Render is DiffRenderState.Loaded l ? l.Highlight : null;

    private bool TryGetPatchContext(int hunkIndex, out Repo repo, out DiffResult diff)
    {
        repo = null!;
        diff = null!;
        var active = _registry.Active.Value;
        if (active == null) return false;
        if (State.Value.Render is not DiffRenderState.Loaded loaded) return false;
        if (!HunkPatchBuilder.CanPatchHunk(loaded.Result)) return false;
        if (hunkIndex < 0 || hunkIndex >= loaded.Result.Hunks.Count) return false;
        repo = active;
        diff = loaded.Result;
        return true;
    }

    private void StartLoad()
    {
        // Any in-flight highlight is for the previous target; invalidate it up front so its
        // result can't land on the diff we're about to load.
        _highlightLane.Bump();

        var target = _target.Value;
        if (target == null)
        {
            Gen.Bump();
            Update(s => s with { Render = new DiffRenderState.Placeholder(EmptyPlaceholder) });
            return;
        }

        var repo = _registry.Active.Value;
        if (repo == null) return;

        if (State.Value.Render is not DiffRenderState.Loaded)
            Update(s => s with { Render = new DiffRenderState.Placeholder(LoadingPlaceholder) });

        var path = target.Path;
        var side = target.Side;
        var commitSha = target.CommitSha;
        RunBackground<DiffRenderState>(
            work: () => (new DiffRenderState.Loaded(_gitService.GetDiff(repo, path, side, commitSha)), null),
            onResult: (result, error) =>
            {
                var render = error != null ? new DiffRenderState.Placeholder(error) : result!;
                Update(s => s with { Render = render });
                if (render is DiffRenderState.Loaded loaded)
                    StartHighlight(repo, loaded.Result, commitSha);
            });
    }

    // Tokenizes the diff's file(s) off-thread and, when done, re-emits the same Loaded state
    // carrying the spans. Runs on the highlight lane (stale results dropped) and only attaches
    // to the still-current diff — an optimistic hunk apply may have swapped Result underneath us.
    private void StartHighlight(Repo repo, DiffResult diff, string? commitSha)
    {
        var git = _gitService;
        RunBackground<DiffHighlight>(
            work: () => (DiffHighlightCoordinator.Compute(git, repo, diff, commitSha), null),
            onResult: (highlight, _) =>
            {
                if (highlight == null) return; // plain rendering — nothing to apply
                if (State.Value.Render is DiffRenderState.Loaded cur && ReferenceEquals(cur.Result, diff))
                    Update(s => s with { Render = new DiffRenderState.Loaded(diff, highlight) });
            },
            lane: _highlightLane);
    }
}
