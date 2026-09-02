using GitBench.Features.Terminal;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// The one place an application exit is decided. Every way out — the title-bar button, Alt+F4,
/// macOS's Quit, the update banner's restart — hands its exit here rather than performing it, so
/// what would be lost is asked about once instead of once per exit.
/// </summary>
public interface IAppExitGate
{
    /// <summary>
    /// Runs <paramref name="exit"/> when nothing is in the way, and otherwise puts the question on
    /// screen and runs it only if the user agrees. Returns whether the application is on its way
    /// out now, for a caller that has to hold something open while the question is answered.
    /// </summary>
    /// <param name="kind">What the app is about to do, which is what the question asks about: the
    /// answer to "is this worth losing a running shell?" reads differently for a quit than for a
    /// restart the user expects to come back from.</param>
    bool RequestExit(AppExitKind kind, Action exit);
}

/// <summary>How the application is ending, for the wording of anything asked on the way out.</summary>
public enum AppExitKind
{
    Quit,
    UpdateRestart,
}

/// <summary>
/// Holds the exit open while a terminal still has a shell, and asks first. A shell mid-build or
/// mid-deploy is not something to lose to a mistyped Cmd+Q or a restart into an update.
/// </summary>
internal sealed class AppExitGate(
    ITerminalSessionStore terminals,
    IUiDispatcher dispatcher,
    IMessageBus bus) : IAppExitGate
{
    public bool RequestExit(AppExitKind kind, Action exit)
    {
        var running = terminals.ReposWithLiveShells();
        if (running.Count == 0)
        {
            exit();
            return true;
        }

        // Posted rather than shown here: an OS close arrives inside the event poll, and the dialog
        // wants a settled view tree. The tick that drains this queue is the next thing the run loop
        // does, so the prompt still lands in the frame the user asked to close.
        dispatcher.Post(() => bus.Broadcast(new ShowDialogMessage(onClose => new ConfirmQuitDialog
        {
            RepoIds = running,
            Kind = kind,
            OnClose = onClose,
            // Past the gate deliberately: the user has just answered the question it asks.
            OnConfirm = exit,
        })));
        return false;
    }
}
