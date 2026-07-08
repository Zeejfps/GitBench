using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Platform;

[SupportedOSPlatform("macos")]
public sealed class MacOSPlatformShell : IPlatformShell
{
    private readonly Context _context;
    private int _pickerOpen;

    public MacOSPlatformShell(Context context)
    {
        _context = context;
    }

    public void PickFolder(string title, Action<string> onPicked) => Pick(title, folder: true, null, onPicked);

    public void PickFile(string title, string? initialDirectory, Action<string> onPicked) =>
        Pick(title, folder: false, initialDirectory, onPicked);

    private void Pick(string title, bool folder, string? initialDirectory, Action<string> onPicked)
    {
        // The picker stays open until it exits — ignore Browse clicks made while one is up.
        if (Interlocked.CompareExchange(ref _pickerOpen, 1, 0) != 0) return;

        // osascript blocks until the user closes the dialog; waiting for it on the UI thread
        // stalls the event loop (beachball). Wait on a worker and post the result back.
        var dispatcher = _context.Require<IUiDispatcher>();
        Task.Run(() =>
        {
            try
            {
                var path = RunPicker(title, folder, initialDirectory);
                if (!string.IsNullOrEmpty(path))
                    dispatcher.Post(() => onPicked(path));
            }
            finally
            {
                Interlocked.Exchange(ref _pickerOpen, 0);
            }
        });
    }

    private static string? RunPicker(string title, bool folder, string? initialDirectory)
    {
        var chooser = folder ? "choose folder" : "choose file";
        var location = string.IsNullOrEmpty(initialDirectory)
            ? ""
            : $" default location (POSIX file \"{EscapeForAppleScript(initialDirectory)}\")";
        var script =
            $"set chosen to {chooser} with prompt \"{EscapeForAppleScript(title)}\"{location}\n" +
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

    private static string EscapeForAppleScript(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

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
