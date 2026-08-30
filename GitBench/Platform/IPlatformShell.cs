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

    /// <summary>
    /// Shows a path in the OS file manager with the entry itself selected, rather than opening it.
    /// </summary>
    /// <remarks>
    /// A default method, not a new abstract member: only two of the platforms can do better than
    /// the fallback, and making the other implementations — production and test alike — each write
    /// out the same parent-folder open would be four files of duplication and ten of ceremony.
    /// The fallback opens the containing folder, which is the honest degradation: the reader ends up
    /// looking at the right directory, just without the entry highlighted.
    /// </remarks>
    void RevealFile(string path)
    {
        var parent = Path.GetDirectoryName(path);
        OpenFolder(string.IsNullOrEmpty(parent) ? path : parent);
    }
}
