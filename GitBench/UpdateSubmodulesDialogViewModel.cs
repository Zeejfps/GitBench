using ZGF.Observable;

namespace GitGui;

internal sealed class UpdateSubmodulesDialogViewModel : IDisposable
{
    public State<bool> Init { get; } = new(true);
    public State<bool> Recursive { get; } = new(false);
    public State<SubmoduleUpdateMode> Mode { get; } = new(SubmoduleUpdateMode.Checkout);

    public AsyncCommand Update { get; }
    public IReadable<string?> Error => Update.Error;

    public event Action? CloseRequested;

    public UpdateSubmodulesDialogViewModel(
        UpdateSubmodulesViewRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var primaryId = request.Primary.Id;
        var target = request.TargetSubmodule;

        Update = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var req = new SubmoduleUpdateRequest(
                    Paths: target is null ? null : new[] { ToRelative(request.Primary.Path, target.Path) },
                    Init: Init.Value,
                    Recursive: Recursive.Value,
                    Mode: Mode.Value);
                var outcome = gitService.UpdateSubmodules(request.Primary, req);
                // A conflict outcome reports !Success, but the update did land — the Operation
                // banner takes over to resolve it, so close and refresh like a clean success
                // rather than surfacing it as an error.
                if (outcome.Success || outcome.HasConflicts) return null;
                return outcome.ErrorMessage ?? "Update submodules failed.";
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
