using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Submodules;

internal sealed class UpdateSubmodulesDialogViewModel : IDialogViewModel
{
    public State<bool> Init { get; } = new(true);
    public State<bool> Recursive { get; } = new(false);
    public State<SubmoduleUpdateMode> Mode { get; } = new(SubmoduleUpdateMode.Checkout);

    public AsyncCommand Update { get; }

    public event Action? CloseRequested;

    public UpdateSubmodulesDialogViewModel(
        UpdateSubmodulesViewRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var primaryId = request.Primary.Id;
        var target = request.TargetSubmodule;

        Update = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var req = new SubmoduleUpdateRequest(
                    Paths: target is null ? null : new[] { ToRelative(request.Primary.Path, target.Path) },
                    Init: Init.Value,
                    Recursive: Recursive.Value,
                    Mode: Mode.Value);
                // A Conflicted outcome means the update did land — the Operation banner takes
                // over to resolve it, so close and refresh like a clean success rather than
                // surfacing it as an error.
                return gitService.UpdateSubmodules(request.Primary, req);
            },
            onSuccess: () =>
            {
                CloseRequested?.Invoke();
                bus.Broadcast(new SubmodulesChangedMessage(primaryId));
                bus.Broadcast(new RefsChangedMessage(primaryId));
            });
    }

    public void Dispose() { }

    private static string ToRelative(string parentRoot, string submoduleAbs)
    {
        try
        {
            var rel = System.IO.Path.GetRelativePath(parentRoot, submoduleAbs);
            return rel.Replace('\\', '/').TrimEnd('/');
        }
        catch
        {
            return submoduleAbs;
        }
    }
}

internal readonly record struct UpdateSubmodulesViewRequest(Repo Primary, Repo? TargetSubmodule);
