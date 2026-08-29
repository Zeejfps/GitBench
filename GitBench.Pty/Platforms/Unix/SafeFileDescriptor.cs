using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitBench.Pty.Platforms.Unix;

/// <summary>
/// Owns an open file descriptor, closing it when it is released.
/// </summary>
/// <remarks>
/// A descriptor rather than a handle is what makes ownership worth a type here: the kernel hands out
/// the lowest free number, so a descriptor closed while another thread is still using it is not a
/// stale number that fails, it is a live number that belongs to something else. Every use of
/// <see cref="Descriptor"/> is inside a <see cref="GatedHandle{THandle}"/>, which is what holds the
/// close back until the last user has left.
/// </remarks>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
internal sealed class SafeFileDescriptor : SafeHandleMinusOneIsInvalid
{
    public SafeFileDescriptor(int descriptor)
        : base(ownsHandle: true) => SetHandle(descriptor);

    public int Descriptor => handle.ToInt32();

    protected override bool ReleaseHandle() => UnixNative.Close(handle.ToInt32()) == 0;
}
