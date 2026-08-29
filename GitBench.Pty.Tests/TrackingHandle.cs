using System.Runtime.InteropServices;

namespace GitBench.Pty.Tests;

/// <summary>A <see cref="SafeHandle"/> that owns nothing and only records whether it was released.</summary>
sealed class TrackingHandle : SafeHandle
{
    int _releases;

    public TrackingHandle()
        : base(IntPtr.Zero, ownsHandle: true) => SetHandle(new IntPtr(1));

    public override bool IsInvalid => false;

    public int Releases => Volatile.Read(ref _releases);

    protected override bool ReleaseHandle()
    {
        Interlocked.Increment(ref _releases);
        return true;
    }
}
