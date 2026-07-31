using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class CreateBranchDialogViewModel : IDialogViewModel
{
    // The seeded ref and the label standing in for it in the field. While the field still reads as
    // the label, the seeded ref is what git gets — so a dialog opened "from the current branch"
    // sends HEAD and resolves at execution time, however the label happens to name it. Only text the
    // user actually typed becomes a name.
    private readonly GitRef _seedRef;
    private readonly string _seedLabel;

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
        GitRef startPoint,
        string startPointLabel,
        string initialName,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IRepoHeadStore head,
        ILocalizationService loc)
    {
        _seedRef = startPoint;
        _seedLabel = startPointLabel;

        Name = new State<string>(initialName);
        StartPoint = new State<string>(startPointLabel);

        var repoId = repo.Id;
        var gate = new Derived<bool>(() => Name.Value.Length > 0 && RefNameRules.IsValid(Name.Value));
        NameStatus = new Derived<FieldStatus?>(() =>
        {
            var s = loc.Strings.Value;
            return RefNameRules.Validate(Name.Value, s, s.RefnameNounBranch);
        });

        Create = AsyncCommand.ForOutcome(
            dispatcher,
            work: () => gitService.CreateBranch(repo, Name.Value, ResolveStartPoint(), Checkout.Value),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repoId));
                CloseRequested?.Invoke();
            },
            gate: gate,
            // With "check out after create" on, this moves HEAD onto the new branch — declare it so
            // the rest of the app knows the name it holds is about to be stale.
            onStart: () => Checkout.Value ? head.BeginMove(repo, Name.Value) : null);
    }

    // Untouched field → the ref the dialog was opened with, so a label reading "main" still sends
    // HEAD. Cleared → HEAD, which is what the field's hint promises. Anything else → the user named
    // something specific and means it literally.
    private GitRef ResolveStartPoint()
    {
        var text = StartPoint.Value;
        if (text == _seedLabel) return _seedRef;
        return text.Length == 0 ? GitRef.Head : GitRef.Named(text);
    }

    public void Dispose() { }
}
