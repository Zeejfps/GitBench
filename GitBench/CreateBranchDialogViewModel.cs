using ZGF.Observable;

namespace GitGui;

internal sealed class CreateBranchDialogViewModel : IDisposable
{
    public State<string> Name { get; } = new(string.Empty);
    public State<string> StartPoint { get; }
    public State<bool> Checkout { get; } = new(true);

    /// <summary>
    /// Live validation of <see cref="Name"/>, surfaced under the branch-name field. Pure and
    /// cheap (no git calls), so it recomputes per keystroke without debouncing. Empty is
    /// reported as neutral — the Create button is gated separately — rather than as an error.
    /// </summary>
    public IReadable<FieldStatus?> NameStatus { get; }

    public AsyncCommand Create { get; }

    public event Action? CloseRequested;

    public CreateBranchDialogViewModel(
        CreateBranchRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        StartPoint = new State<string>(request.SuggestedStartPoint);

        var repoId = request.Repo.Id;
        var gate = new Derived<bool>(() => Name.Value.Length > 0 && RefNameRules.Validate(Name.Value, "Branch") is null);
        NameStatus = new Derived<FieldStatus?>(() => RefNameRules.Validate(Name.Value, "Branch"));

        Create = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var name = Name.Value;
                var startPoint = StartPoint.Value;
                if (startPoint.Length == 0) startPoint = "HEAD";
                var checkout = Checkout.Value;
                var outcome = gitService.CreateBranch(request.Repo, name, startPoint, checkout);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Create branch failed.");
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

internal readonly record struct CreateBranchRequest(Repo Repo, string SuggestedStartPoint);
