namespace GitGui;

public sealed class NoopPlatformShell : IPlatformShell
{
    public string? PickFolder(string title)
    {
        Console.WriteLine($"[PlatformShell] No native picker for this OS. Title: {title}");
        return null;
    }

    public void OpenFolder(string path)
    {
        Console.WriteLine($"[PlatformShell] No native OpenFolder for this OS. Path: {path}");
    }

    public void OpenTerminal(string path)
    {
        Console.WriteLine($"[PlatformShell] No native OpenTerminal for this OS. Path: {path}");
    }
}
