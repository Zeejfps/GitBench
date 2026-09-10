using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitBench.Platform;

/// <summary>
/// The Recycle Bin, through the shell's own file operation, so an entry lands there with the origin
/// that "Restore" reads and a folder goes in as one item rather than as its contents.
/// </summary>
/// <remarks>
/// <para>
/// The paths are handed over as a native double-null-terminated block rather than as marshalled
/// struct fields: the struct is not blittable with <c>string</c> members, and this build is
/// ahead-of-time compiled.
/// </para>
/// <para>
/// One case cannot be reported honestly: a volume with no Recycle Bin — a network share, some
/// removable media, a file past the bin's size cap — deletes outright under <c>FOF_ALLOWUNDO</c>
/// rather than failing, which is what Explorer warns about before doing the same thing. Suppressing
/// the confirmation is what makes this a silent operation, and it is the same choice that loses the
/// warning.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsTrash
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCTW operation);

    public static void Move(string path)
    {
        // The list is terminated by an empty string, so the buffer ends in two nulls: one for the
        // path, one for the list.
        var from = Marshal.StringToHGlobalUni(path + '\0');
        try
        {
            var operation = new SHFILEOPSTRUCTW
            {
                Func = FO_DELETE,
                From = from,
                Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };

            var result = SHFileOperation(ref operation);
            if (result != 0)
                throw new IOException(
                    $"The shell refused to recycle '{path}' (0x{result:X8}): "
                    + new Win32Exception(result).Message);
            if (operation.AnyOperationsAborted != 0)
                throw new IOException($"Recycling '{path}' was cancelled.");
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}
