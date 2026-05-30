using ZGF.Observable;

namespace GitGui;

internal sealed class CreateTagDialogViewModel : IDisposable
{
    public State<string> Name { get; } = new(string.Empty);
    public State<string> Message { get; } = new(string.Empty);
    public State<bool> PushToAllRemotes { get; } = new(true);

    public AsyncCommand Create { get; }

    public event Action? CloseRequested;

    public CreateTagDialogViewModel(
        CreateTagRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var repoId = request.Repo.Id;
        var gate = new Derived<bool>(() => Name.Value.Trim().Length > 0);

        Create = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var name = Name.Value.Trim();
                var message = Message.Value;
                var push = PushToAllRemotes.Value;
                var outcome = gitService.CreateTag(request.Repo, name, message, request.Sha, push);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Create tag failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repoId));
                CloseRequested?.Invoke();
            },
            gate: gate);
    }

    public void SetMessage(string message) => Message.Value = message;

    public void Dispose() { }
}

internal readonly record struct CreateTagRequest(Repo Repo, string Sha);
