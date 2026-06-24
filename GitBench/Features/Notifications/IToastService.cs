using ZGF.Observable;

namespace GitBench.Features.Notifications;

/// <summary>
/// Single source of truth for the live toast stack. Owns each toast's lifetime (the expiry timer
/// for <see cref="ToastLifetime.Timed"/>), so view models only ever project <see cref="Active"/> —
/// they never schedule their own dismissal. Callers raise toasts either through this surface or by
/// broadcasting <see cref="GitBench.Messages.ShowToastMessage"/>.
/// </summary>
public interface IToastService
{
    /// <summary>The currently-visible toasts, oldest first. Swaps as toasts arrive and expire.</summary>
    IReadable<IReadOnlyList<Toast>> Active { get; }

    /// <summary>Adds a toast and returns its assigned id (so a caller can dismiss it early).</summary>
    ToastId Show(ToastIntent intent);

    /// <summary>Begins dismissing the toast: flags it exiting (its card animates out) and removes it
    /// shortly after. A no-op if it's already gone or already exiting.</summary>
    void Dismiss(ToastId id);

    /// <summary>Whether this toast is animating out. A card binds its exit animation to this; flips
    /// true once when <see cref="Dismiss"/> is called.</summary>
    IReadable<bool> Exiting(ToastId id);
}
