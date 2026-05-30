using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

public record DiffTarget(string Path, DiffSide Side, string? CommitSha = null);

public abstract record DiffRenderState
{
    public sealed record Placeholder(string Text) : DiffRenderState;
    public sealed record Loaded(DiffResult Result) : DiffRenderState;
}

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

    public IReadable<DiffRenderState> RenderState { get; }
    public IReadable<string?> OpError { get; }

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

        RenderState = Slice(s => s.Render);
        OpError = Slice(s => s.OpError);

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
            Update(s => s with { Render = new DiffRenderState.Loaded(diff with { Hunks = remainingHunks }) });
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
                        Update(s => s with { Render = new DiffRenderState.Loaded(original) });
                    bus.Broadcast(new WorkingTreeChangedMessage(repoId));
                    return;
                }
                Update(s => s with { OpError = null });
                bus.Broadcast(new WorkingTreeChangedMessage(repoId));
            });
        });
    }

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
                Update(s => s with
                {
                    Render = error != null ? new DiffRenderState.Placeholder(error) : result!,
                }));
    }
}
