using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Notifications;

/// <summary>
/// Backs one <see cref="ToastCard"/>. A toast is immutable once shown, so this holds plain values
/// plus the two commands a card needs: dismiss, and (when present) invoke the toast's action and
/// dismiss it.
/// </summary>
internal sealed class ToastItemViewModel : IDisposable
{
    private readonly Toast _toast;

    public ToastSeverity Severity => _toast.Intent.Severity;
    public string Message => _toast.Intent.Message;
    public bool HasAction => _toast.Intent.Action != null;
    public string ActionLabel => _toast.Intent.Action?.Label ?? string.Empty;

    public ICommand Dismiss { get; }
    public ICommand InvokeAction { get; }

    public ToastItemViewModel(Toast toast, IToastService toasts)
    {
        _toast = toast;
        Dismiss = new Command(() => toasts.Dismiss(toast.Id));
        InvokeAction = new Command(() =>
        {
            toast.Intent.Action?.Invoke();
            toasts.Dismiss(toast.Id);
        });
    }

    public void Dispose() { }
}
