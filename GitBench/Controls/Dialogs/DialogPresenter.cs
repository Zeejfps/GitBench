using GitBench.Features.Operations;
using GitBench.Messages;
using ZGF.Gui;

namespace GitBench.Controls.Dialogs;

public sealed class DialogPresenter : IViewBehavior
{
    private readonly DialogSurfaceView _dialogSurfaceView;
    private readonly Context _windowContext;
    private IMessageBus? _bus;
    private Action<ShowDialogMessage>? _onShowDialog;

    public DialogPresenter(Context ctx, DialogSurfaceView dialogSurfaceView)
    {
        _windowContext = ctx;
        _dialogSurfaceView = dialogSurfaceView;
    }

    public void Attach(View view)
    {
        var bus = _windowContext.Get<IMessageBus>();
        if (bus is null) return;

        _bus = bus;
        _onShowDialog = m => ShowDialog(m.CreateDialog(_windowContext, OnDialogClosed));
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
        var dialog = new OperationErrorDialog
        {
            Title = m.Title,
            Message = m.Message,
            Recovery = m.Recovery,
            OnClose = OnDialogClosed,
        }.WithController<DialogKbmController>().BuildView(_windowContext);

        // A failure raised from inside an open dialog stacks the error on top so the user can read
        // it, act on it (copy, remove a stale lock), then return to the dialog to retry; a failure
        // with nothing on screen (e.g. a background push) just shows.
        if (_dialogSurfaceView.IsShowing) _dialogSurfaceView.PushDialog(dialog);
        else _dialogSurfaceView.ShowDialog(dialog);
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
