using System.Diagnostics;
using System.Runtime.Versioning;

namespace GitBench.Platform;

[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformShell : IPlatformShell
{
    private readonly string? _opener;
    private readonly string? _terminal;
    private readonly string? _trash;

    public LinuxPlatformShell()
    {
        _opener = FindOnPath("xdg-open");
        _terminal = ResolveTerminal();
        _trash = FindOnPath("gio");
    }

    public void OpenFolder(string path) => Open(path);

    public void OpenFile(string path) => Open(path);

    // xdg-open dispatches http(s) URLs to the default browser, same as files/folders.
    public void OpenUrl(string url) => Open(url);

    /// <summary>
    /// Through <c>gio trash</c>, which is glib's implementation of the freedesktop trash spec — the
    /// same one the file managers use, so an entry lands with the <c>.trashinfo</c> that "Restore"
    /// reads. Writing the spec out here instead would mean getting the per-volume <c>.Trash-$uid</c>
    /// directories and the cross-device copy right for no gain on any desktop that has glib, and
    /// there is no trash at all on one that does not.
    /// </summary>
    public bool CanMoveToTrash => _trash != null;

    public void MoveToTrash(string path)
    {
        if (_trash == null)
            throw new NotSupportedException("gio is not on PATH, so there is no trash to move to.");

        var psi = new ProcessStartInfo(_trash)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("trash");
        // Ends the options, so a file whose name begins with a dash is a path and not a flag.
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(path);

        using var process = Process.Start(psi)
            ?? throw new IOException($"Could not run gio to trash '{path}'.");
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode == 0) return;

        throw new IOException(error.Length > 0 ? error : $"gio could not trash '{path}'.");
    }

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
