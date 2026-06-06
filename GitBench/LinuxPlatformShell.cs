using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace GitBench;

[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformShell : IPlatformShell
{
    private readonly string? _zenity;
    private readonly string? _kdialog;
    private readonly string? _opener;
    private readonly string? _terminal;

    public LinuxPlatformShell()
    {
        _zenity = FindOnPath("zenity");
        _kdialog = FindOnPath("kdialog");
        _opener = FindOnPath("xdg-open");
        _terminal = ResolveTerminal();

        if (_zenity == null && _kdialog == null)
            Console.WriteLine("[PlatformShell] No zenity/kdialog found on PATH; folder picker is unavailable.");
    }

    public string? PickFolder(string title)
    {
        if (_zenity != null)
            return RunPicker(_zenity, ["--file-selection", "--directory", $"--title={title}"]);
        if (_kdialog != null)
            return RunPicker(_kdialog, ["--getexistingdirectory", ".", "--title", title]);

        Console.WriteLine($"[PlatformShell] No native picker available. Title: {title}");
        return null;
    }

    public void OpenFolder(string path) => Open(path);

    public void OpenFile(string path) => Open(path);

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

    private static string? RunPicker(string tool, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = tool,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // Cancel returns a non-zero exit code with empty output for both zenity and kdialog.
            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.WriteLine($"[PlatformShell] {Path.GetFileName(tool)} exited {process.ExitCode}: {stderr.Trim()}");
                return null;
            }

            var path = stdout.Trim();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[PlatformShell] {Path.GetFileName(tool)} failed: {e.Message}");
            return null;
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
