using GitBench.Controls;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// One rendered row of the file browser. A directory carries whether it is open; a file cannot, so
/// there is no row that claims to be an expanded file. Produced by <see cref="FileBrowserTree"/>;
/// the pane paints this sequence and the view model navigates the same one, so the two can never
/// disagree about what row 12 is.
/// </summary>
/// <remarks>
/// <see cref="IsIgnored"/> and <see cref="IsHidden"/> are display facts, never filters — the browser
/// exists to show what git's own views do not, so an ignored file is dimmed rather than dropped
/// unless the reader has explicitly asked for it to be dropped.
/// </remarks>
internal abstract record FileBrowserRow(
    string FullPath,
    string Name,
    int Depth,
    bool IsIgnored,
    bool IsHidden,
    bool IsLink,
    TreeGuides Guides)
{
    public sealed record Directory(
        string FullPath,
        string Name,
        int Depth,
        bool IsIgnored,
        bool IsHidden,
        bool IsLink,
        TreeGuides Guides,
        bool IsExpanded)
        : FileBrowserRow(FullPath, Name, Depth, IsIgnored, IsHidden, IsLink, Guides);

    public sealed record File(
        string FullPath,
        string Name,
        int Depth,
        bool IsIgnored,
        bool IsHidden,
        bool IsLink,
        TreeGuides Guides)
        : FileBrowserRow(FullPath, Name, Depth, IsIgnored, IsHidden, IsLink, Guides);
}
