using ZGF.AppUtils;

namespace GitBench.Platform;

/// <summary>
/// Puts DiffDino in the Linux application menu, which Velopack's AppImage does not do: on Windows
/// and macOS its installer creates the Start-menu / Applications entry, but an AppImage has no
/// install step. So on every launch from an AppImage the app writes (or corrects) a
/// <c>DiffDino.desktop</c> under <c>$XDG_DATA_HOME/applications</c> pointing at the AppImage it
/// was started from. That covers a hand-downloaded AppImage, one the user has moved since, and an
/// entry from install.sh, which writes the same file and must stay byte-for-byte identical to
/// <see cref="Build"/> or every launch rewrites it.
/// <para>
/// An entry without the <see cref="ManagedMarker"/> line is the user's own and is never touched.
/// <c>TryExec</c> makes menus hide the entry once the AppImage is deleted, so there is no stale
/// launcher to clean up after a manual uninstall.
/// </para>
/// </summary>
internal static class LinuxDesktopEntry
{
    public const string DesktopFileName = "DiffDino.desktop";
    public const string IconFileName = "DiffDino.png";
    public const string ManagedMarker = "X-DiffDino-Managed=true";

    /// <summary>
    /// A no-op unless running from an AppImage, whose runtime exports its own path as
    /// <c>APPIMAGE</c> (a dev build or <c>dotnet run</c> has none, and must not register itself).
    /// Never throws: a missing menu entry is not worth failing a launch over.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsLinux()) return;

        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrEmpty(appImage) || !Path.IsPathRooted(appImage)) return;

        try
        {
            Ensure(appImage, DataHome(), PathUtils.ResolveLocalPath("Assets/app_icon.png"));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not update the application menu entry: {e.Message}");
        }
    }

    /// <summary>Returns whether the entry was written.</summary>
    internal static bool Ensure(string appImagePath, string dataHome, string iconSource)
    {
        var iconPath = Path.Combine(dataHome, "icons", IconFileName);
        if (!File.Exists(iconPath) && File.Exists(iconSource))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
            File.Copy(iconSource, iconPath, overwrite: true);
        }

        var desktopPath = Path.Combine(dataHome, "applications", DesktopFileName);
        var contents = Build(appImagePath, iconPath);
        if (File.Exists(desktopPath))
        {
            var existing = File.ReadAllText(desktopPath);
            if (existing == contents) return false;
            if (!existing.Split('\n').Any(line => line.Trim() == ManagedMarker)) return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(desktopPath)!);
        var part = desktopPath + ".part";
        File.WriteAllText(part, contents);
        File.Move(part, desktopPath, overwrite: true);
        return true;
    }

    internal static string Build(string appImagePath, string iconPath) => string.Join('\n',
    [
        "[Desktop Entry]",
        "Type=Application",
        "Name=DiffDino",
        "Comment=A desktop Git client",
        "Exec=" + ExecValue(appImagePath),
        "TryExec=" + StringValue(appImagePath),
        "Icon=" + StringValue(iconPath),
        "StartupWMClass=DiffDino",
        "Categories=Development;RevisionControl;",
        "Terminal=false",
        ManagedMarker,
        "",
    ]);

    /// <summary>A .desktop string value escapes its backslashes (Desktop Entry spec, "Possible value types").</summary>
    private static string StringValue(string value) => value.Replace(@"\", @"\\");

    /// <summary>
    /// Exec is stricter: the path is double-quoted with <c>\ " ` $</c> backslash-escaped inside the
    /// quotes, then string-escaped as above, and a literal <c>%</c> doubled so it is not read as a
    /// field code.
    /// </summary>
    private static string ExecValue(string path)
    {
        var quoted = new System.Text.StringBuilder(path.Length + 8);
        foreach (var c in path)
        {
            if (c is '\\' or '"' or '`' or '$') quoted.Append('\\');
            if (c == '%') quoted.Append('%');
            quoted.Append(c);
        }
        return StringValue("\"" + quoted + "\"");
    }

    private static string DataHome()
    {
        // The XDG Base Directory spec says a relative XDG_DATA_HOME is invalid and to be ignored.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)) return xdg;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }
}
