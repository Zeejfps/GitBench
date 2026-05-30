using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace GitGui;

[SupportedOSPlatform("macos")]
public sealed class MacOSPlatformShell : IPlatformShell
{
    public string? PickFolder(string title)
    {
        var escapedTitle = title.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script =
            $"set chosen to choose folder with prompt \"{escapedTitle}\"\n" +
            "return POSIX path of chosen";

        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            ArgumentList = { "-e", script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi);
        if (process == null) return null;

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            // User cancel returns "User canceled. (-128)" on stderr; treat as null.
            if (stderr.Contains("-128")) return null;
            Console.WriteLine($"[PlatformShell] osascript failed ({process.ExitCode}): {stderr.Trim()}");
            return null;
        }

        var path = stdout.Trim();
        if (string.IsNullOrEmpty(path)) return null;

        // POSIX path of a folder ends with a trailing slash; trim it (but keep "/" itself).
        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');

        return path;
    }

    public void OpenFolder(string path)
    {
        var psi = new ProcessStartInfo("/usr/bin/open");
        psi.ArgumentList.Add(path);
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
