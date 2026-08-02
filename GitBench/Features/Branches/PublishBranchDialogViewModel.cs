using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class PublishBranchDialogViewModel : IDialogViewModel
{
    private readonly PublishBranchRequest _request;
    private readonly IGitBranchOperations _gitBranches;
    private readonly IGitRemoteOperations _gitRemotes;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;

    private readonly State<IReadOnlyList<string>> _remotes = new(Array.Empty<string>());
    private readonly State<string?> _loadError = new(null);

    public IReadable<IReadOnlyList<string>> Remotes => _remotes;
    public State<string> SelectedRemote { get; } = new(string.Empty);
    public State<bool> SetUpstream { get; } = new(true);

    public AsyncCommand Publish { get; }

    /// <summary>Load-time inline message (no remotes configured). The publish failure itself
    /// surfaces in the operation-error dialog, not here.</summary>
    public IReadable<string?> LoadError => _loadError;

    public event Action? CloseRequested;

    public PublishBranchDialogViewModel(
        PublishBranchRequest request,
        IGitBranchOperations gitBranches,
        IGitRemoteOperations gitRemotes,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc)
    {
        _request = request;
        _gitBranches = gitBranches;
        _gitRemotes = gitRemotes;
        _dispatcher = dispatcher;
        _bus = bus;
        _loc = loc;

        var repoId = request.Repo.Id;

        var gate = new Derived<bool>(() => !string.IsNullOrEmpty(SelectedRemote.Value));

        Publish = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var remote = SelectedRemote.Value;
                var setUpstream = SetUpstream.Value;
                var local = _request.LocalBranch;
                var outcome = _gitBranches.PublishBranch(_request.Repo, local, remote, local, setUpstream);
                return outcome;
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repoId));
                CloseRequested?.Invoke();
            },
            gate: gate);

        LoadRemotes();
    }

    private void LoadRemotes()
    {
        var repo = _request.Repo;
        var service = _gitRemotes;
        var dispatcher = _dispatcher;

        Task.Run(() =>
        {
            IReadOnlyList<string> remotes;
            try { remotes = service.GetRemoteNames(repo); }
            catch { remotes = Array.Empty<string>(); }

            dispatcher.Post(() =>
            {
                _remotes.Value = remotes;
                if (remotes.Count == 0)
                {
                    _loadError.Value = _loc.Strings.Value.BranchesPublishErrorNoRemotes;
                }
                else
                {
                    _loadError.Value = null;
                    var preferred = remotes.FirstOrDefault(o => o == "origin") ?? remotes[0];
                    SelectedRemote.Value = preferred;
                }
            });
        });
    }

    public void Dispose() { }
}
