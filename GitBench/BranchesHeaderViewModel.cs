using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

internal sealed class BranchesHeaderViewModel : ViewModelBase<BranchesHeaderState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;

    public IReadable<string?> BranchName { get; }
    public IReadable<bool> IsDetached { get; }

    // Per-repo cache of the last push status, so a switch shows the target repo's branch name
    // immediately rather than leaving the previous repo's name up until the query returns.
    private readonly RepoSnapshotCache<PushStatus> _cache = new();
    private Guid? _lastRepoId;

    public BranchesHeaderViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
        : base(dispatcher, BranchesHeaderState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;

        BranchName = Slice(s => s.BranchName);
        IsDetached = Slice(s => s.IsDetached);

        Subscriptions.Add(_registry.Active.Subscribe(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<CommitCreatedMessage>(_ => Reload()));
    }

    private void Reload()
    {
        var repo = _registry.Active.Value;
        if (repo == null)
        {
            _lastRepoId = null;
            Update(_ => BranchesHeaderState.Initial);
            return;
        }

        // On a switch to a different repo, paint the cached branch name right away. Same-repo
        // reloads (refs/commit changes) leave the live value up and just refresh in the
        // background. A cache miss keeps the current display until the query lands.
        if (repo.Id != _lastRepoId && _cache.TryGet(repo.Id, out var cached) && cached != null)
            Apply(cached);
        _lastRepoId = repo.Id;

        var repoId = repo.Id;
        RunBackground<PushStatus>(
            () =>
            {
                try { return (_gitService.GetPushStatus(repo), null); }
                catch { return (new PushStatus(null, false, 0, 0, false), null); }
            },
            (status, _) =>
            {
                // Cache before the active-repo guard so a late load still warms switch-back.
                _cache.Set(repoId, status!);
                if (_registry.Active.Value?.Id != repoId) return;
                Apply(status!);
            });
    }

    private void Apply(PushStatus status) =>
        Update(_ => status.IsDetached
            ? new BranchesHeaderState("(detached HEAD)", true)
            : new BranchesHeaderState(status.CurrentBranchName, false));
}

internal sealed record BranchesHeaderState(string? BranchName, bool IsDetached)
{
    public static BranchesHeaderState Initial { get; } = new(null, false);
}
