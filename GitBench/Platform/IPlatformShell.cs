namespace GitBench.Platform;

public interface IPlatformShell
{
    // Shows the OS folder picker without blocking the UI thread, then invokes onPicked on the
    // UI thread with the chosen path. Not invoked when the user cancels or no picker exists.
    void PickFolder(string title, Action<string> onPicked);
    void OpenFolder(string path);
    void OpenTerminal(string path);
    // Opens a file with the OS's default application (e.g. for "Open in editor").
    void OpenFile(string path);
    // Opens a URL in the user's default browser.
    void OpenUrl(string url);
}
