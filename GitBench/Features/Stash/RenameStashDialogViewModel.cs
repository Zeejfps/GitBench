using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Stash;

internal sealed class RenameStashDialogViewModel : IDialogViewModel
{
    public State<string> Message { get; }

    public AsyncCommand Rename { get; }

    public event Action? CloseRequested;

    public RenameStashDialogViewModel(
        RenameStashRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Message = new State<string>(request.CurrentMessage);

        var repoId = request.Repo.Id;
        var index = request.Index;
        var oldMessage = request.CurrentMessage;

        var gate = new Derived<bool>(() => Message.Value.Length > 0 && Message.Value != oldMessage);

        Rename = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.RenameStash(request.Repo, index, Message.Value);
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

internal readonly record struct RenameStashRequest(Repo Repo, int Index, string CurrentMessage);
