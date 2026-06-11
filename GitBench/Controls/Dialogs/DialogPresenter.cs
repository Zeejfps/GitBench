using GitBench.Features.Operations;
using GitBench.Messages;
using ZGF.Gui;

namespace GitBench.Controls.Dialogs;

public sealed class DialogPresenter : IViewBehavior
{
    private readonly DialogSurfaceView _dialogSurfaceView;
    private IMessageBus? _bus;
    private Context? _windowContext;
    private Action<ShowDialogMessage>? _onShowDialog;

    public DialogPresenter(DialogSurfaceView dialogSurfaceView)
    {
        _dialogSurfaceView = dialogSurfaceView;
    }

    public void Attach(View view)
    {
        var context = ViewContexts.Require(view);
        var bus = context.Get<IMessageBus>();
        if (bus is null) return;

        _bus = bus;
        _windowContext = context;
        _onShowDialog = m => ShowDialog(m.CreateDialog(_windowContext!, OnDialogClosed));
        bus.Subscribe(_onShowDialog);
        bus.Subscribe<ShowOperationErrorMessage>(OnShowOperationError);
    }

    public void Detach(View view)
    {
        if (_bus is null) return;
        if (_onShowDialog != null) _bus.Unsubscribe(_onShowDialog);
        _bus.Unsubscribe<ShowOperationErrorMessage>(OnShowOperationError);
        _bus = null;
        _onShowDialog = null;
    }

    private void OnShowOperationError(ShowOperationErrorMessage m)
    {
        // Defensive: a blank body means no actionable info — showing the chrome alone is
        // worse than dropping the message. Callers should produce a real message (the
        // git CLI almost always writes *something* on failure); this guard is a backstop
        // for the few paths where we couldn't extract any meaningful text.
        if (string.IsNullOrWhiteSpace(m.Message)) return;
        ShowDialog(new OperationErrorDialog
        {
            Title = m.Title,
            Message = m.Message,
            OnClose = OnDialogClosed,
        }.BuildView(_windowContext!));
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
