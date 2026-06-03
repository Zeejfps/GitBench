using ZGF.Observable;

namespace GitGui;

internal sealed class RenameBranchDialogViewModel : IDisposable
{
    public State<string> Name { get; }
    public State<bool> Force { get; } = new(false);

    /// <summary>Live refname validation surfaced under the name field. See <see cref="BranchNameRules"/>.</summary>
    public IReadable<FieldStatus?> NameStatus { get; }

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

        NameStatus = new Derived<FieldStatus?>(() => BranchNameRules.Validate(Name.Value));
        var gate = new Derived<bool>(() =>
            Name.Value.Length > 0 && Name.Value != oldName && BranchNameRules.Validate(Name.Value) is null);

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
