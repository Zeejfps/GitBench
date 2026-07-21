using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitBench.Platform;

[SupportedOSPlatform("macos")]
public sealed class MacOSPlatformShell : IPlatformShell
{
    public void OpenFolder(string path)
    {
        var psi = new ProcessStartInfo("/usr/bin/open");
        psi.ArgumentList.Add(path);
        using var _ = Process.Start(psi);
    }

    public void OpenFile(string path)
    {
        var psi = new ProcessStartInfo("/usr/bin/open");
        psi.ArgumentList.Add(path);
        using var _ = Process.Start(psi);
    }

    public void OpenUrl(string url)
    {
        // `open` hands http(s) URLs to the default browser.
        var psi = new ProcessStartInfo("/usr/bin/open");
        psi.ArgumentList.Add(url);
        using var _ = Process.Start(psi);
    }

    public void OpenTerminal(string path)
    {
        var psi = new ProcessStartInfo("/usr/bin/open");
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add("Terminal");
        psi.ArgumentList.Add(path);
        using var _ = Process.Start(psi);
    }
}
