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
    /// Whether this platform can put a path in the OS's trash rather than unlinking it. Asked
    /// before the deletion is offered, because it decides what the reader is agreeing to: a move
    /// they can undo from the Finder, or a file that is gone.
    /// </summary>
    bool CanMoveToTrash => false;

    /// <summary>
    /// Moves a path to the OS's trash, throwing with the OS's own words when it cannot.
    /// </summary>
    /// <remarks>
    /// Throws rather than reporting best-effort like the rest of this interface, and the difference
    /// is the point: every other member here hands something to another application, and its failure
    /// costs the reader a window that did not open. This one is the operation itself, and a
    /// deletion that quietly did nothing is worse than one that says why.
    /// </remarks>
    void MoveToTrash(string path) =>
        throw new NotSupportedException("This platform has no trash to move a file to.");

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
