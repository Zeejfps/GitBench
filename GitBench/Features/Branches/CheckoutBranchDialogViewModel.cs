using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class CheckoutBranchDialogViewModel : IDialogViewModel
{
    public State<string> Name { get; }
    public State<bool> Track { get; } = new(true);

    /// <summary>Live refname validation surfaced under the name field. See <see cref="RefNameRules"/>.</summary>
    public IReadable<FieldStatus?> NameStatus { get; }

    public AsyncCommand Checkout { get; }

    public event Action? CloseRequested;

    public CheckoutBranchDialogViewModel(
        CheckoutRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IRepoHeadStore head,
        ILocalizationService loc)
    {
        Name = new State<string>(request.SuggestedLocalName);

        var repoId = request.Repo.Id;
        NameStatus = new Derived<FieldStatus?>(() =>
        {
            var s = loc.Strings.Value;
            return RefNameRules.Validate(Name.Value, s, s.RefnameNounBranch);
        });
        var gate = new Derived<bool>(() => Name.Value.Length > 0 && RefNameRules.IsValid(Name.Value));

        Checkout = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.CheckoutRemoteBranch(
                    request.Repo, 
                    Name.Value, 
                    request.RemoteName, 
                    request.RemoteBranchName, 
                    Track.Value
                );
                return outcome;
            },
            // Close before broadcasting: an error broadcast swaps in the error dialog, and a stale
            // Close() afterwards would dismiss that brand-new dialog instead of this one. Both paths
            // close, so the ordering holds either way.
            onSuccess: () =>
            {
                CloseRequested?.Invoke();
                bus.Broadcast(new RefsChangedMessage(repoId));
            },
            gate: gate,
            onError: error =>
            {
                CloseRequested?.Invoke();
                bus.Broadcast(new ShowOperationErrorMessage(loc.Strings.Value.BranchesErrorCheckoutFailed, error));
            },
            // `checkout -b` lands HEAD on the new local branch, so this is a branch switch like any
            // other — declare it, or the toolbar keeps seeding the branch being left behind.
            onStart: () => head.BeginMove(request.Repo, Name.Value));
    }

    public void Dispose() { }
}
