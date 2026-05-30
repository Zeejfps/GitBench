using ZGF.Observable;

namespace GitGui;

internal sealed class DeinitSubmoduleDialogViewModel : IDisposable
{
    public State<bool> Force { get; } = new(false);

    public AsyncCommand Deinit { get; }

    public event Action? CloseRequested;

    public DeinitSubmoduleDialogViewModel(
        DeinitSubmoduleViewRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var primaryId = request.Primary.Id;
        var submodulePath = ToRelative(request.Primary.Path, request.Submodule.Path);

        Deinit = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var force = Force.Value;
                var outcome = gitService.DeinitSubmodule(request.Primary, submodulePath, force);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Deinit submodule failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new SubmodulesChangedMessage(primaryId));
                bus.Broadcast(new WorkingTreeChangedMessage(primaryId));
                CloseRequested?.Invoke();
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

internal readonly record struct DeinitSubmoduleViewRequest(Repo Primary, Repo Submodule);
