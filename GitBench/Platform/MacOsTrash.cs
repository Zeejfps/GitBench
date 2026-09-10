using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static ZGF.Rendering.Metal.Objc;

namespace GitBench.Platform;

/// <summary>
/// The Finder's trash, through <c>NSFileManager trashItemAtURL:</c> — the same call the Finder's own
/// delete makes, so the item lands with its "Put Back" origin recorded and comes home to where it
/// was rather than to wherever it is dragged.
/// </summary>
/// <remarks>
/// Not <c>osascript</c> telling the Finder to delete: that route is Apple-events automation, which
/// on any modern macOS raises a consent prompt the first time and fails outright when the answer was
/// once "Don't Allow" — a permission dialog is not what someone expects from a Delete they just
/// confirmed. The direct call needs no entitlement outside a sandbox.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static class MacOsTrash
{
    // objc_msgSend returning BOOL. Objc's own overloads are the ones Metal needed; this signature —
    // three object arguments, a BOOL back — is not among them, and a wrong return marshalling here
    // reads a success out of an arbitrary register.
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool msg_Bool(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);

    public static void Move(string path)
    {
        // Every object below is autoreleased, and this runs on a worker thread, which has no pool of
        // its own — without one they leak for the life of the process.
        var pool = objc_autoreleasePoolPush();
        var error = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(error, IntPtr.Zero);

            var url = msg_IntPtr(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path));
            var manager = msg_IntPtr(Class("NSFileManager"), Sel("defaultManager"));
            if (url == IntPtr.Zero || manager == IntPtr.Zero)
                throw new IOException($"Could not ask the Finder to trash '{path}'.");

            var moved = msg_Bool(
                manager,
                Sel("trashItemAtURL:resultingItemURL:error:"),
                url,
                IntPtr.Zero,
                error);
            if (moved) return;

            throw new IOException(Description(Marshal.ReadIntPtr(error))
                ?? $"Could not move '{path}' to the Trash.");
        }
        finally
        {
            Marshal.FreeHGlobal(error);
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>What the NSError says, in the user's language, or null when there is no error object
    /// to ask — a <c>NO</c> return without one is allowed by the contract and says nothing.</summary>
    private static string? Description(IntPtr error)
    {
        if (error == IntPtr.Zero) return null;
        var description = msg_IntPtr(error, Sel("localizedDescription"));
        if (description == IntPtr.Zero) return null;
        var utf8 = msg_IntPtr(description, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return msg_IntPtr(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }
}
