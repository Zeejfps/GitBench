using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitBench.Pty.Platforms.Windows;

/// <summary>
/// Owns an HPCON, closing the pseudo-console — and with it the terminal side of both pipes — when
/// it is released.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePseudoConsoleHandle(IntPtr pseudoConsole)
        : base(ownsHandle: true) => SetHandle(pseudoConsole);

    protected override bool ReleaseHandle()
    {
        WindowsNative.ClosePseudoConsole(handle);
        return true;
    }
}
