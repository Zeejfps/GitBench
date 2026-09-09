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

/// <summary>
/// A move of the browser's preview, as the content panel's trail needs to hear it.
/// </summary>
/// <param name="From">Where the browser was, or null when it was showing nothing — which is not a
/// place, and not something to come back to.</param>
/// <param name="IsMove">False when the file asked for is the one already on screen. The panel may
/// still have to swing over to it from another tab, but there is nothing new to come back from.</param>
internal readonly record struct FileBrowserMove(FileBrowserPlace? From, bool IsMove);
