namespace GitBench.Features.Notifications;

/// <summary>
/// Semantic class of a toast — drives its accent color and icon. A closed set so the
/// view's icon/color mapping is an exhaustive switch.
/// </summary>
public enum ToastSeverity
{
    Success,
    Info,
    Warning,
    Error,
}

/// <summary>
/// How long a toast stays up. A closed union so "auto-dismiss after a delay" and "stays until
/// the user dismisses it" are the only two shapes — there's no sentinel duration that secretly
/// means sticky.
/// </summary>
public abstract record ToastLifetime
{
    private ToastLifetime() { }

    /// <summary>Auto-dismisses once <see cref="Duration"/> elapses.</summary>
    public sealed record Timed(TimeSpan Duration) : ToastLifetime;

    /// <summary>Stays until the user dismisses it (or its action resolves it).</summary>
    public sealed record Sticky : ToastLifetime;
}

/// <summary>
/// Stable identity for a live toast, and the reconciliation key the host list uses. A distinct
/// type so a toast id can't be passed where some other id is expected.
/// </summary>
public readonly record struct ToastId(long Value);

/// <summary>An optional button on a toast (Undo / Retry / Details). Invoking it dismisses the toast.</summary>
public sealed record ToastAction(string Label, Action Invoke);

/// <summary>
/// What a caller asks to be shown. The service assigns the <see cref="ToastId"/> and owns the
/// expiry timer, so call sites never fabricate ids or schedule their own dismissal. The factories
/// fix a consistent default lifetime per severity (transient for success/info, sticky for the ones
/// the user should acknowledge).
/// </summary>
public sealed record ToastIntent(
    ToastSeverity Severity,
    string Message,
    ToastLifetime Lifetime,
    ToastAction? Action = null)
{
    private static ToastLifetime DefaultTimed => new ToastLifetime.Timed(TimeSpan.FromSeconds(4));

    public static ToastIntent Success(string message, ToastAction? action = null) =>
        new(ToastSeverity.Success, message, DefaultTimed, action);

    public static ToastIntent Info(string message, ToastAction? action = null) =>
        new(ToastSeverity.Info, message, DefaultTimed, action);

    public static ToastIntent Warning(string message, ToastAction? action = null) =>
        new(ToastSeverity.Warning, message, new ToastLifetime.Sticky(), action);

    public static ToastIntent Error(string message, ToastAction? action = null) =>
        new(ToastSeverity.Error, message, new ToastLifetime.Sticky(), action);
}

/// <summary>A live toast: a caller's <see cref="ToastIntent"/> plus the service-assigned id.</summary>
public sealed record Toast(ToastId Id, ToastIntent Intent);
