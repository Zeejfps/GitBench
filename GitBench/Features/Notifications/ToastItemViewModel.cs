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

    // Transient (timed) toasts auto-dismiss, so they don't carry a close button — only sticky ones
    // the user must acknowledge do.
    public bool ShowDismiss => _toast.Intent.Lifetime is ToastLifetime.Sticky;

    // Seconds the toast stays up, for the countdown bar; 0 for sticky toasts (no countdown).
    public float DurationSeconds =>
        _toast.Intent.Lifetime is ToastLifetime.Timed timed ? (float)timed.Duration.TotalSeconds : 0f;

    // True while the toast is animating out; the card reverses its enter tween off this.
    public IReadable<bool> Exiting { get; }

    public ICommand Dismiss { get; }
    public ICommand InvokeAction { get; }

    public ToastItemViewModel(Toast toast, IToastService toasts)
    {
        _toast = toast;
        Exiting = toasts.Exiting(toast.Id);
        Dismiss = new Command(() => toasts.Dismiss(toast.Id));
        InvokeAction = new Command(() =>
        {
            toast.Intent.Action?.Invoke();
            toasts.Dismiss(toast.Id);
        });
    }

    public void Dispose() { }
}
