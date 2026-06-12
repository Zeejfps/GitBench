namespace GitBench.Infrastructure;

/// <summary>
/// Contract for dialog view models hosted by the <c>Dialog</c> widget: the widget roots the
/// VM for the view's mounted lifetime and routes <see cref="CloseRequested"/> to the
/// dialog's OnClose.
/// </summary>
internal interface IDialogViewModel : IDisposable
{
    event Action? CloseRequested;
}
