using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitBench.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformShell : IPlatformShell
{
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
