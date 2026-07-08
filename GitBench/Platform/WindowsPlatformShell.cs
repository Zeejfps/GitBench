using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitBench.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformShell : IPlatformShell
{
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int ERROR_CANCELLED_HRESULT = unchecked((int)0x800704C7);
    private const uint CLSCTX_INPROC_SERVER = 0x1;

    // CLSID_FileOpenDialog
    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    // IID_IFileOpenDialog
    private static readonly Guid IidFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");
    // IID_IShellItem2. Requesting the base IID_IShellItem from SHCreateItemFromParsingName
    // returns E_NOINTERFACE on some systems; IShellItem2 derives from IShellItem, so the pointer
    // is vtable-compatible with SetFolder (which only needs an IShellItem).
    private static readonly Guid IidShellItem2 = new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");

    // Runs on the calling (UI) thread: IFileDialog::Show is modal and pumps its own message
    // loop, so the app stays responsive, and COM wants the UI thread's STA anyway.
    public void PickFolder(string title, Action<string> onPicked)
    {
        var path = ShowFileDialog(title, pickFolder: true);
        if (!string.IsNullOrEmpty(path))
            onPicked(path);
    }

    public void PickFile(string title, string? initialDirectory, Action<string> onPicked)
    {
        var path = ShowFileDialog(title, pickFolder: false, initialDirectory);
        if (!string.IsNullOrEmpty(path))
            onPicked(path);
    }

    // Calls IFileDialog/IShellItem methods directly through the COM vtable. This avoids
    // the [ComImport] RCW pattern, which depends on runtime IL generation and is disabled
    // when PublishAot is set.
    private static unsafe string? ShowFileDialog(string title, bool pickFolder, string? initialDirectory = null)
    {
        CoCreateInstance(in ClsidFileOpenDialog, IntPtr.Zero, CLSCTX_INPROC_SERVER, in IidFileOpenDialog, out var pDialog);
        try
        {
            var vtbl = *(IntPtr**)pDialog;

            // IFileDialog vtable (after IUnknown 0..2):
            //   3 Show, 9 SetOptions, 10 GetOptions, 12 SetFolder, 17 SetTitle, 20 GetResult
            var show       = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtbl[3];
            var setOptions = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vtbl[9];
            var getOptions = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vtbl[10];
            var setFolder  = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtbl[12];
            var setTitle   = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtbl[17];
            var getResult  = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[20];

            var extraOptions = FOS_FORCEFILESYSTEM | (pickFolder ? FOS_PICKFOLDERS : 0);
            uint options;
            Marshal.ThrowExceptionForHR(getOptions(pDialog, &options));
            Marshal.ThrowExceptionForHR(setOptions(pDialog, options | extraOptions));

            // Best-effort: an unopenable path just leaves the dialog at its default location.
            if (!string.IsNullOrEmpty(initialDirectory)
                && SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, in IidShellItem2, out var pFolder) == 0
                && pFolder != IntPtr.Zero)
            {
                try { setFolder(pDialog, pFolder); }
                finally { Release(pFolder); }
            }

            fixed (char* pTitle = title)
            {
                Marshal.ThrowExceptionForHR(setTitle(pDialog, pTitle));
            }

            var hr = show(pDialog, IntPtr.Zero);
            if (hr == ERROR_CANCELLED_HRESULT) return null;
            Marshal.ThrowExceptionForHR(hr);

            IntPtr pItem;
            Marshal.ThrowExceptionForHR(getResult(pDialog, &pItem));
            try
            {
                var itemVtbl = *(IntPtr**)pItem;
                // IShellItem vtable: 5 = GetDisplayName
                var getDisplayName = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)itemVtbl[5];

                IntPtr pPath;
                Marshal.ThrowExceptionForHR(getDisplayName(pItem, SIGDN_FILESYSPATH, &pPath));
                try
                {
                    return Marshal.PtrToStringUni(pPath);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pPath);
                }
            }
            finally
            {
                Release(pItem);
            }
        }
        finally
        {
            Release(pDialog);
        }
    }

    private static unsafe void Release(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        var vtbl = *(IntPtr**)p;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
        release(p);
    }

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        in Guid riid,
        out IntPtr ppv);

    public void OpenFolder(string path)
    {
        var psi = new ProcessStartInfo("explorer.exe");
        psi.ArgumentList.Add(path);
        using var _ = Process.Start(psi);
    }

    public void OpenFile(string path)
    {
        // UseShellExecute routes through the shell so the file opens in its default app.
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        using var _ = Process.Start(psi);
    }

    public void OpenUrl(string url)
    {
        // UseShellExecute lets the shell hand the URL to the default browser.
        var psi = new ProcessStartInfo(url) { UseShellExecute = true };
        using var _ = Process.Start(psi);
    }

    public void OpenTerminal(string path)
    {
        // Windows Terminal first; fall back to cmd.exe if wt isn't installed.
        try
        {
            var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            wt.ArgumentList.Add("-d");
            wt.ArgumentList.Add(path);
            using var _ = Process.Start(wt);
            return;
        }
        catch (Win32Exception) { /* wt not available */ }

        var cmd = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = path,
            UseShellExecute = true,
        };
        using var __ = Process.Start(cmd);
    }
}
