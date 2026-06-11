using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Commits;

internal sealed class CreateTagDialogViewModel : IDisposable
{
    public State<string> Name { get; } = new(string.Empty);
    public State<string> Message { get; } = new(string.Empty);
    public State<bool> PushToAllRemotes { get; } = new(true);

    /// <summary>Live refname validation surfaced under the tag-name field. See <see cref="RefNameRules"/>.</summary>
    public IReadable<FieldStatus?> NameStatus { get; }

    public AsyncCommand Create { get; }

    public event Action? CloseRequested;

    public CreateTagDialogViewModel(
        CreateTagRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var repoId = request.Repo.Id;
        // Validate the trimmed value: Create trims before handing the name to git, so a
        // surrounding space shouldn't read as an error the user can't see the cause of.
        NameStatus = new Derived<FieldStatus?>(() => RefNameRules.Validate(Name.Value.Trim(), "Tag"));
        var gate = new Derived<bool>(() =>
            Name.Value.Trim().Length > 0 && RefNameRules.Validate(Name.Value.Trim(), "Tag") is null);

        Create = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var name = Name.Value.Trim();
                var message = Message.Value;
                var push = PushToAllRemotes.Value;
                var outcome = gitService.CreateTag(request.Repo, name, message, request.Sha, push);
                return outcome;
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
