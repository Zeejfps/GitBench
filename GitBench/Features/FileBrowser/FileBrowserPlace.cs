namespace GitBench.Features.FileBrowser;

/// <summary>
/// A place in the browser, as the navigation history holds it: the file that was on screen, the row
/// the tree was on inside it, and the line the reader was reading.
/// </summary>
/// <remarks>
/// The row key is kept beside the path rather than instead of it, because a declaration's key is not
/// a path and the file it names has to survive the tree no longer listing that declaration — a jump
/// back to a file whose parse has moved on still lands on the file.
/// </remarks>
internal sealed record FileBrowserPlace(string AbsolutePath, string? RowKey, int Line);
