namespace GitBench.Platform;

public interface IPlatformShell
{
    void OpenFolder(string path);
    void OpenTerminal(string path);
    // Opens a file with the OS's default application (e.g. for "Open in editor").
    void OpenFile(string path);
    // Opens a URL in the user's default browser. Best effort: callers are typically UI event
    // handlers, so a launch failure is logged and swallowed, never thrown.
    void OpenUrl(string url);
}
