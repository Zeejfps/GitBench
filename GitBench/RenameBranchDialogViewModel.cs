using ZGF.Observable;

namespace GitGui;

internal sealed class RenameBranchDialogViewModel : IDisposable
{
    public State<string> Name { get; }
    public State<bool> Force { get; } = new(false);

    public AsyncCommand Rename { get; }

    public event Action? CloseRequested;

    public RenameBranchDialogViewModel(
        RenameBranchRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Name = new State<string>(request.CurrentName);

        var repoId = request.Repo.Id;
        var oldName = request.CurrentName;

        var gate = new Derived<bool>(() => Name.Value.Length > 0 && Name.Value != oldName);

        Rename = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var newName = Name.Value;
                var force = Force.Value;
                var outcome = gitService.RenameBranch(request.Repo, oldName, newName, force);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Rename failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repoId));
                CloseRequested?.Invoke();
            },
            gate: gate);
    }

    public void Dispose() { }
}

internal readonly record struct RenameBranchRequest(Repo Repo, string CurrentName);
