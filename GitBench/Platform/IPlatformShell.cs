namespace GitBench.Platform;

public interface IPlatformShell
{
    string? PickFolder(string title);
    void OpenFolder(string path);
    void OpenTerminal(string path);
    // Opens a file with the OS's default application (e.g. for "Open in editor").
    void OpenFile(string path);
    // Opens a URL in the user's default browser.
    void OpenUrl(string url);
}
