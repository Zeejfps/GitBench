using ZGF.Observable;

namespace GitGui;

internal sealed class AddSubmoduleDialogViewModel : IDisposable
{
    public State<string> Url { get; } = new(string.Empty);
    public State<string> Path { get; } = new(string.Empty);
    public State<string> Branch { get; } = new(string.Empty);
    public State<bool> Force { get; } = new(false);

    public AsyncCommand Add { get; }

    public event Action? CloseRequested;

    public AddSubmoduleDialogViewModel(
        AddSubmoduleViewRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var primaryId = request.Primary.Id;

        var gate = new Derived<bool>(() =>
            Url.Value.Trim().Length > 0 && Path.Value.Trim().Length > 0);

        Add = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var url = Url.Value.Trim();
                var path = Path.Value.Trim();
                var branch = Branch.Value.Trim();
                var force = Force.Value;
                var req = new SubmoduleAddRequest(
                    Url: url,
                    Path: path,
                    Branch: branch.Length > 0 ? branch : null,
                    Force: force);
                var outcome = gitService.AddSubmodule(request.Primary, req);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Add submodule failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new SubmodulesChangedMessage(primaryId));
                bus.Broadcast(new WorkingTreeChangedMessage(primaryId));
                CloseRequested?.Invoke();
            },
            gate: gate);
    }

    public void Dispose() { }
}

internal readonly record struct AddSubmoduleViewRequest(Repo Primary);
