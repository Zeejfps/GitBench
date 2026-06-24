using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.Notifications;

/// <summary>
/// Projects <see cref="IToastService.Active"/> into a stable, keyed list of
/// <see cref="ToastItemViewModel"/> for the host's <c>Each</c> — a card's view model survives while
/// its toast is on screen and is disposed when the toast expires. Holds no state of its own; the
/// service is the source of truth.
/// </summary>
internal sealed class ToastsViewModel : IDisposable
{
    private readonly KeyedViewModelList<Toast, ToastId, ToastItemViewModel> _items;

    public ObservableList<ToastItemViewModel> Items => _items.Items;

    public ToastsViewModel(IToastService toasts)
    {
        _items = new KeyedViewModelList<Toast, ToastId, ToastItemViewModel>(
            toasts.Active,
            toast => toast.Id,
            toast => new ToastItemViewModel(toast, toasts));
    }

    public void Dispose() => _items.Dispose();
}
