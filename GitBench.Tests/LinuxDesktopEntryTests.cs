using GitBench.Platform;
using Xunit;
using Zdev.Common;

namespace GitBench.Tests;

public sealed class LinuxDesktopEntryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gitbench-desktop-" + Guid.NewGuid().ToString("N"));

    private string DataHome => Path.Combine(_root, "share");
    private string DesktopPath => Path.Combine(DataHome, "applications", LinuxDesktopEntry.DesktopFileName);
    private string IconPath => Path.Combine(DataHome, "icons", LinuxDesktopEntry.IconFileName);
    private string IconSource => Path.Combine(_root, "app_icon.png");

    public LinuxDesktopEntryTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(IconSource, "png");
    }

    public void Dispose()
    {
        DirectoryTree.Delete(_root);
    }

    private static string ExecLine(string contents) =>
        contents.Split('\n').Single(line => line.StartsWith("Exec="));

    [Fact]
    public void FirstLaunchWritesTheEntryAndIcon()
    {
        Assert.True(LinuxDesktopEntry.Ensure("/home/me/.local/bin/DiffDino.AppImage", DataHome, IconSource));

        var contents = File.ReadAllText(DesktopPath);
        Assert.Equal(LinuxDesktopEntry.Build("/home/me/.local/bin/DiffDino.AppImage", IconPath), contents);
        Assert.Contains("Exec=\"/home/me/.local/bin/DiffDino.AppImage\"\n", contents);
        Assert.Contains("TryExec=/home/me/.local/bin/DiffDino.AppImage\n", contents);
        Assert.Contains($"Icon={IconPath}\n", contents);
        Assert.Equal("png", File.ReadAllText(IconPath));
    }

    [Fact]
    public void AnUpToDateEntryIsLeftAlone()
    {
        LinuxDesktopEntry.Ensure("/opt/DiffDino.AppImage", DataHome, IconSource);

        Assert.False(LinuxDesktopEntry.Ensure("/opt/DiffDino.AppImage", DataHome, IconSource));
    }

    [Fact]
    public void AMovedAppImageRepointsTheEntry()
    {
        LinuxDesktopEntry.Ensure("/old/DiffDino.AppImage", DataHome, IconSource);

        Assert.True(LinuxDesktopEntry.Ensure("/new/DiffDino.AppImage", DataHome, IconSource));
        Assert.Equal("Exec=\"/new/DiffDino.AppImage\"", ExecLine(File.ReadAllText(DesktopPath)));
    }

    [Fact]
    public void AnEntryTheUserWroteIsNeverOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopPath)!);
        const string mine = "[Desktop Entry]\nType=Application\nName=My DiffDino\nExec=/somewhere/else\n";
        File.WriteAllText(DesktopPath, mine);

        Assert.False(LinuxDesktopEntry.Ensure("/opt/DiffDino.AppImage", DataHome, IconSource));
        Assert.Equal(mine, File.ReadAllText(DesktopPath));
    }

    [Fact]
    public void AnExistingIconIsKept()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IconPath)!);
        File.WriteAllText(IconPath, "custom");

        LinuxDesktopEntry.Ensure("/opt/DiffDino.AppImage", DataHome, IconSource);

        Assert.Equal("custom", File.ReadAllText(IconPath));
    }

    [Fact]
    public void ExecQuotesAndEscapesThePath()
    {
        // Inside the quotes \ " ` $ are backslash-escaped, then every backslash is doubled again
        // by the .desktop string escaping; % is doubled so it is not read as a field code.
        var contents = LinuxDesktopEntry.Build("/a b/$x\"y`z\\w 100%/D.AppImage", "/i.png");

        Assert.Equal(@"Exec=""/a b/\\$x\\""y\\`z\\\\w 100%%/D.AppImage""", ExecLine(contents));
        Assert.Contains(@"TryExec=/a b/$x""y`z\\w 100%/D.AppImage" + "\n", contents);
    }
}
