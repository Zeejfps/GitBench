using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace GitBench.Pty;

/// <summary>
/// A handle shared by a blocking native call and the thread that tears the session down:
/// <see cref="Close"/> stops new calls reaching the handle and hands the actual close to the last
/// call still inside it, so a handle is never closed underneath a thread using it and a caller that
/// arrives after teardown is turned away rather than faulted.
/// </summary>
/// <remarks>
/// It closes a handle safely; it does not wake anyone. <see cref="Close"/> hands the real close to
/// the last caller to leave, so a thread parked in a blocking call never leaves and the handle is
/// never released — the gate turns "closed underneath a blocked reader" into "leaked descriptor and
/// leaked thread", which is quieter and no better. Whatever ends the blocking call is the platform
/// session's to arrange: ConPTY ends the child and the pipe breaks, and a Unix session has to reach
/// every holder of the slave or wake its reader some other way.
/// </remarks>
internal sealed class GatedHandle<THandle>(THandle handle)
    where THandle : SafeHandle
{
    readonly THandle _handle = handle;
    int _users;
    int _closed;

    /// <summary>Takes a use of the handle, or reports that the gate is closed.</summary>
    public bool TryEnter([NotNullWhen(true)] out THandle? entered)
    {
        Interlocked.Increment(ref _users);

        if (Volatile.Read(ref _closed) != 0)
        {
            Leave();
            entered = null;
            return false;
        }

        entered = _handle;
        return true;
    }

    /// <summary>Gives up a use taken by <see cref="TryEnter"/>.</summary>
    public void Leave()
    {
        if (Interlocked.Decrement(ref _users) == 0 && Volatile.Read(ref _closed) != 0)
            _handle.Dispose();
    }

    /// <summary>Closes the gate, and the handle too once the last use has left.</summary>
    public void Close()
    {
        Interlocked.Exchange(ref _closed, 1);

        if (Volatile.Read(ref _users) == 0)
            _handle.Dispose();
    }
}
