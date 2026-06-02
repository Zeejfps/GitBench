using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

// Drives the repo-level "submodules out of date" banner — the safety net for the case where
// the parent's recorded submodule pointer has moved but the submodule's working tree hasn't
// followed (a dirty/conflicted submodule that pull --recurse-submodules couldn't reconcile,
// or one that was never initialized-then-updated). Mirrors DetachedHeadBannerViewModel:
// self-contained, always subscribed, recomputes on the same change signals.
internal sealed class SubmoduleStatusBannerViewModel : ViewModelBase<SubmoduleStatusBannerState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;

    public IReadable<int> OutdatedCount { get; }
    public Command UpdateSubmodules { get; }

    public SubmoduleStatusBannerViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
        : base(dispatcher, SubmoduleStatusBannerState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;

        OutdatedCount = Slice(s => s.OutdatedCount);
        UpdateSubmodules = new Command(DoUpdateSubmodules);

        Subscriptions.Add(_registry.Active.Subscribe(_ => Reload()));
        // A pull/fetch (RefsChanged) or HEAD swap can move the recorded pointer; add/update/
        // deinit fire SubmodulesChanged (which is also what clears the banner after the user
        // clicks "Update submodules"). These are exactly the events that flip submodule status —
        // WorkingTreeChanged (staging/saves) is deliberately not one, so it's left out to avoid
        // re-running `git submodule status` on every edit.
        Subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<SubmodulesChangedMessage>(_ => Reload()));
    }

    // Opens the same dialog as "Update all submodules…" on the repo, pre-targeting every
    // submodule. The dialog (not a silent run) is deliberate: an out-of-date submodule is
    // often dirty or conflicted, and the user may need to pick init / merge / rebase there.
    private void DoUpdateSubmodules()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new UpdateSubmodulesDialog(repo, null, onClose)));
    }

    private void Reload()
    {
        var repo = _registry.Active.Value;
        if (repo == null)
        {
            Update(_ => SubmoduleStatusBannerState.Initial);
            return;
        }

        var repoId = repo.Id;
        var service = _gitService;
        RunBackground<int>(
            () =>
            {
                try
                {
                    var infos = service.ListSubmodules(repo, out _);
                    var outdated = 0;
                    foreach (var info in infos)
                    {
                        // Modified = checked-out SHA differs from the parent's recorded pointer;
                        // MergeConflict = unresolved conflict in the submodule. Both mean the
                        // submodule isn't where the main repo says it should be. NotInitialized
                        // is intentionally excluded — a deliberately-uninitialized submodule
                        // shouldn't nag on every refresh.
                        if (info.Status is SubmoduleStatus.Modified or SubmoduleStatus.MergeConflict)
                            outdated++;
                    }
                    return (outdated, null);
                }
                catch { return (0, null); }
            },
            (outdated, _) =>
            {
                if (_registry.Active.Value?.Id != repoId) return;
                Update(_ => new SubmoduleStatusBannerState(outdated));
            });
    }
}

internal sealed record SubmoduleStatusBannerState(int OutdatedCount)
{
    public static SubmoduleStatusBannerState Initial { get; } = new(0);
}
