using GitBench.App;
using GitBench.Features.Repos;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Editor;

/// <summary>Holds the exit open while a file has edits that are not on disk, and asks first. Wraps
/// the inner gate rather than replacing it.</summary>
internal sealed class UnsavedDocumentsExitGate(
    IAppExitGate inner,
    IDocumentStore documents,
    IRepoRegistry registry,
    IUiDispatcher dispatcher,
    IMessageBus bus) : IAppExitGate
{
    public bool RequestExit(AppExitKind kind, Action exit)
    {
        var unsaved = documents.Unsaved();
        if (unsaved.Count == 0) return inner.RequestExit(kind, exit);

        dispatcher.Post(() => bus.Broadcast(new ShowDialogMessage(onClose => new UnsavedChangesDialog
        {
            Files = unsaved.Select(f => UnsavedFileName.Of(registry, f)).ToArray(),
            Kind = UnsavedChangesKind.Quit,
            OnClose = onClose,
            OnConfirm = () => inner.RequestExit(kind, exit),
        })));
        return false;
    }
}
