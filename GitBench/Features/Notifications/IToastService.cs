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

    /// <summary>Removes the toast with this id if it's still visible; a no-op otherwise.</summary>
    void Dismiss(ToastId id);
}
