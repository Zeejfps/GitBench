using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitBench.Pty.Platforms.Windows;

/// <summary>
/// The attribute list that hands a child process its pseudo-console, and the unmanaged block it
/// lives in. Must outlive the CreateProcessW call that reads it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcThreadAttributeList : IDisposable
{
    IntPtr _block;

    ProcThreadAttributeList(IntPtr block) => _block = block;

    public IntPtr Block => _block;

    public static unsafe ProcThreadAttributeList ForPseudoConsole(IntPtr pseudoConsole)
    {
        nuint size = 0;
        WindowsNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        if (size == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var block = Marshal.AllocHGlobal((nint)size);

        if (!WindowsNative.InitializeProcThreadAttributeList(block, 1, 0, ref size))
        {
            var error = Marshal.GetLastPInvokeError();
            Marshal.FreeHGlobal(block);
            throw new Win32Exception(error);
        }

        var list = new ProcThreadAttributeList(block);

        if (!WindowsNative.UpdateProcThreadAttribute(
                block,
                0,
                WindowsNative.ProcThreadAttributePseudoConsole,
                (void*)pseudoConsole,
                (nuint)IntPtr.Size,
                null,
                null))
        {
            var error = Marshal.GetLastPInvokeError();
            list.Dispose();
            throw new Win32Exception(error);
        }

        return list;
    }

    public void Dispose()
    {
        var block = Interlocked.Exchange(ref _block, IntPtr.Zero);

        if (block == IntPtr.Zero)
            return;

        WindowsNative.DeleteProcThreadAttributeList(block);
        Marshal.FreeHGlobal(block);
    }
}
