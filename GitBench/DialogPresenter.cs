using ZGF.Gui;

namespace GitGui;

public sealed class DialogPresenter : IViewBehavior
{
    private readonly DialogSurfaceView _dialogSurfaceView;

    public DialogPresenter(DialogSurfaceView dialogSurfaceView)
    {
        _dialogSurfaceView = dialogSurfaceView;
    }

    public void AttachToContext(View view, Context context)
    {
        var bus = context.Get<IMessageBus>();
        if (bus is null) return;

        bus.Subscribe<ShowDialogMessage>(m => ShowDialog(m.CreateDialog(OnDialogClosed)));
        bus.Subscribe<AddRepoMessage>(_ => ShowDialog(new AddRepoDialog(OnDialogClosed)));
        bus.Subscribe<ShowOperationErrorMessage>(OnShowOperationError);
    }

    public void DetachFromContext(View view, Context context)
    {
    }

    private void OnShowOperationError(ShowOperationErrorMessage m)
    {
        // Defensive: a blank body means no actionable info — showing the chrome alone is
        // worse than dropping the message. Callers should produce a real message (the
        // git CLI almost always writes *something* on failure); this guard is a backstop
        // for the few paths where we couldn't extract any meaningful text.
        if (string.IsNullOrWhiteSpace(m.Message)) return;
        ShowDialog(new OperationErrorDialog(m.Title, m.Message, OnDialogClosed));
    }

    private void ShowDialog(View dialog)
    {
        _dialogSurfaceView.ShowDialog(dialog);
    }

    private void OnDialogClosed()
    {
        _dialogSurfaceView.HideDialog();
    }
}
