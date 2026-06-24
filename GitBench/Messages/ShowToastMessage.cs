using GitBench.Features.Notifications;

namespace GitBench.Messages;

/// <summary>
/// Requests a toast be shown. Mirrors <see cref="ShowDialogMessage"/>/<see cref="ShowOperationErrorMessage"/>:
/// the toast service subscribes, so any code that already holds the bus can surface a toast without
/// taking a new dependency. The service mints the id and owns the lifetime.
/// </summary>
public readonly record struct ShowToastMessage(ToastIntent Intent);
