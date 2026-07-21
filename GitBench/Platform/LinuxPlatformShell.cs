using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitBench.Platform;

[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformShell : IPlatformShell
{
    private readonly string? _opener;
    private readonly string? _terminal;

    public LinuxPlatformShell()
    {
        _opener = FindOnPath("xdg-open");
        _terminal = ResolveTerminal();
    }

    public void OpenFolder(string path) => Open(path);

    public void OpenFile(string path) => Open(path);

    // xdg-open dispatches http(s) URLs to the default browser, same as files/folders.
    public void OpenUrl(string url) => Open(url);

    public void OpenTerminal(string path)
    {
        if (_terminal == null)
        {
            Console.WriteLine($"[PlatformShell] No terminal emulator found on PATH. Path: {path}");
            return;
        }

        try
        {
            // Most emulators open a shell in the launch CWD, so set it rather than guessing per-terminal flags.
            using var _ = Process.Start(new ProcessStartInfo(_terminal) { WorkingDirectory = path, UseShellExecute = false });
        }
        catch (Exception e)
        {
            Console.WriteLine($"[PlatformShell] Failed to open terminal '{_terminal}': {e.Message}");
        }
    }

    private void Open(string path)
    {
        if (_opener == null)
        {
            Console.WriteLine($"[PlatformShell] xdg-open not found on PATH. Path: {path}");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(_opener) { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            using var _ = Process.Start(psi);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[PlatformShell] xdg-open failed for '{path}': {e.Message}");
        }
    }

    private static string? ResolveTerminal()
    {
        var preferred = Environment.GetEnvironmentVariable("TERMINAL");
        if (!string.IsNullOrEmpty(preferred))
        {
            var resolved = Path.IsPathRooted(preferred) && File.Exists(preferred) ? preferred : FindOnPath(preferred);
            if (resolved != null) return resolved;
        }

        string[] candidates =
        [
            "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal",
            "kitty", "alacritty", "wezterm", "tilix", "xterm",
        ];
        foreach (var candidate in candidates)
        {
            var resolved = FindOnPath(candidate);
            if (resolved != null) return resolved;
        }
        return null;
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            var full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
