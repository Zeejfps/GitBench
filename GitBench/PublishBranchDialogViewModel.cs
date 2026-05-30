using ZGF.Observable;

namespace GitGui;

internal sealed class PublishBranchDialogViewModel : IDisposable
{
    private readonly PublishBranchRequest _request;
    private readonly IGitService _gitService;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;

    private readonly State<IReadOnlyList<string>> _remotes = new(Array.Empty<string>());
    private readonly State<string?> _loadError = new(null);

    public IReadable<IReadOnlyList<string>> Remotes => _remotes;
    public State<string> SelectedRemote { get; } = new(string.Empty);
    public State<bool> SetUpstream { get; } = new(true);

    public AsyncCommand Publish { get; }
    public IReadable<string?> ErrorMessage { get; }

    public event Action? CloseRequested;

    public PublishBranchDialogViewModel(
        PublishBranchRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        _request = request;
        _gitService = gitService;
        _dispatcher = dispatcher;
        _bus = bus;

        var repoId = request.Repo.Id;

        var gate = new Derived<bool>(() => !string.IsNullOrEmpty(SelectedRemote.Value));

        Publish = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var remote = SelectedRemote.Value;
                var setUpstream = SetUpstream.Value;
                var local = _request.LocalBranch;
                var outcome = _gitService.PublishBranch(_request.Repo, local, remote, local, setUpstream);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Publish failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repoId));
                CloseRequested?.Invoke();
            },
            gate: gate);

        ErrorMessage = new Derived<string?>(() => _loadError.Value ?? Publish.Error.Value);

        LoadRemotes();
    }

    private void LoadRemotes()
    {
        var repo = _request.Repo;
        var service = _gitService;
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
                    _loadError.Value = "No remotes configured. Add one with: git remote add origin <url>";
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
