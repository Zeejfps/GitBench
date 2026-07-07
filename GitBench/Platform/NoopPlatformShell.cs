namespace GitBench.Platform;

public sealed class NoopPlatformShell : IPlatformShell
{
    public void PickFolder(string title, Action<string> onPicked)
    {
        Console.WriteLine($"[PlatformShell] No native picker for this OS. Title: {title}");
    }

    public void OpenFolder(string path)
    {
        Console.WriteLine($"[PlatformShell] No native OpenFolder for this OS. Path: {path}");
    }

    public void OpenTerminal(string path)
    {
        Console.WriteLine($"[PlatformShell] No native OpenTerminal for this OS. Path: {path}");
    }

    public void OpenFile(string path)
    {
        Console.WriteLine($"[PlatformShell] No native OpenFile for this OS. Path: {path}");
    }

    public void OpenUrl(string url)
    {
        Console.WriteLine($"[PlatformShell] No native OpenUrl for this OS. Url: {url}");
    }
}
