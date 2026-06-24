using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class CreateBranchDialogViewModel : IDialogViewModel
{
    public State<string> Name { get; }
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
        Repo repo,
        string suggestedStartPoint,
        string initialName,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc)
    {
        Name = new State<string>(initialName);
        StartPoint = new State<string>(suggestedStartPoint);

        var repoId = repo.Id;
        var gate = new Derived<bool>(() => Name.Value.Length > 0 && RefNameRules.IsValid(Name.Value));
        NameStatus = new Derived<FieldStatus?>(() =>
        {
            var s = loc.Strings.Value;
            return RefNameRules.Validate(Name.Value, s, s.RefnameNounBranch);
        });

        Create = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var name = Name.Value;
                var startPoint = StartPoint.Value;
                if (startPoint.Length == 0) startPoint = "HEAD";
                var checkout = Checkout.Value;
                var outcome = gitService.CreateBranch(repo, name, startPoint, checkout);
                return outcome;
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
