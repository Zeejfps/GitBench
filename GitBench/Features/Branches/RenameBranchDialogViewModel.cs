using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class RenameBranchDialogViewModel : IDialogViewModel
{
    public State<string> Name { get; }
    public State<bool> Force { get; } = new(false);

    /// <summary>Live refname validation surfaced under the name field. See <see cref="RefNameRules"/>.</summary>
    public IReadable<FieldStatus?> NameStatus { get; }

    public AsyncCommand Rename { get; }

    public event Action? CloseRequested;

    public RenameBranchDialogViewModel(
        RenameBranchRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IRepoStatusStore status,
        IRepoHeadStore head,
        ILocalizationService loc)
    {
        Name = new State<string>(request.CurrentName);

        var repoId = request.Repo.Id;
        var oldName = request.CurrentName;

        NameStatus = new Derived<FieldStatus?>(() =>
        {
            var s = loc.Strings.Value;
            return RefNameRules.Validate(Name.Value, s, s.RefnameNounBranch);
        });
        var gate = new Derived<bool>(() =>
            Name.Value.Length > 0 && Name.Value != oldName && RefNameRules.IsValid(Name.Value));

        Rename = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var newName = Name.Value;
                var force = Force.Value;
                var outcome = gitService.RenameBranch(request.Repo, oldName, newName, force);
                return outcome;
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repoId));
                CloseRequested?.Invoke();
            },
            gate: gate,
            // Renaming the checked-out branch doesn't move HEAD to a different commit, but it does
            // change what HEAD is called — which is the thing every "current branch" reader holds.
            // Renaming any other branch leaves HEAD alone and declares nothing.
            onStart: () => status.Active.Value.EffectiveBranchName == oldName
                ? head.BeginMove(request.Repo, Name.Value)
                : null);
    }

    public void Dispose() { }
}

internal readonly record struct RenameBranchRequest(Repo Repo, string CurrentName);
