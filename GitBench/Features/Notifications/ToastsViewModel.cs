using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.Notifications;

/// <summary>
/// Projects <see cref="IToastService.Active"/> into a stable, keyed list for the slot's <c>Each</c> —
/// a chip's view model survives while its toast is on screen and is disposed when the toast expires.
/// Holds no state of its own; the service is the source of truth.
/// </summary>
internal sealed class ToastsViewModel : IDisposable
{
    private readonly IReadable<IReadOnlyList<Toast>> _onScreen;
    private readonly KeyedViewModelList<Toast, ToastId, ToastItemViewModel> _items;

    public ObservableList<ToastItemViewModel> Items => _items.Items;

    public ToastsViewModel(IToastService toasts)
    {
        _onScreen = toasts.Active.Select(Newest);
        _items = new KeyedViewModelList<Toast, ToastId, ToastItemViewModel>(
            _onScreen,
            toast => toast.Id,
            toast => new ToastItemViewModel(toast, toasts));
    }

    // The status bar is one line, so only the newest toast is on screen; the ones behind it keep
    // running their own expiry timers. Timed toasts arrive and expire in the same order, so nothing
    // stale pops back — a sticky one does, once whatever covered it has gone.
    private static IReadOnlyList<Toast> Newest(IReadOnlyList<Toast> active) =>
        active.Count == 0 ? Array.Empty<Toast>() : new[] { active[^1] };

    public void Dispose()
    {
        _items.Dispose();
        (_onScreen as IDisposable)?.Dispose();
    }
}
