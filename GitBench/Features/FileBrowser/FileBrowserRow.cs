using GitBench.Controls;
using GitBench.Features.CodeIntel;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// One rendered row of the file browser: a directory, a file, or a declaration inside a file.
/// Produced by <see cref="FileBrowserTree"/>; the pane paints this sequence and the view model
/// navigates the same one, so the two can never disagree about what row 12 is.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsIgnored"/> and <see cref="IsHidden"/> are display facts, never filters — the browser
/// exists to show what git's own views do not, so an ignored file is dimmed rather than dropped
/// unless the reader has explicitly asked for it to be dropped.
/// </para>
/// <para>
/// A file and the declarations inside it share one <see cref="FullPath"/>, so the cursor tracks
/// <see cref="RowKey"/> instead. Paths cannot contain a newline on either platform, which is what
/// makes a symbol key unmistakable for a path — and what lets <see cref="FileBrowserViewModel"/>
/// decline to persist one. A declaration's half of that key is its containment chain rather than
/// its line, so both the cursor and a collapsed declaration survive an edit further up the file.
/// </para>
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
    /// <summary>What the cursor holds. A path for anything on disk.</summary>
    public virtual string RowKey => FullPath;

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

    /// <summary>A file. Expandable when the parser has a grammar for it — which is known from the
    /// name alone, so the chevron costs no read; whether anything is under it is only learned by
    /// opening it, and a file with no declarations opens to nothing.</summary>
    public sealed record File(
        string FullPath,
        string Name,
        int Depth,
        bool IsIgnored,
        bool IsHidden,
        bool IsLink,
        TreeGuides Guides,
        bool IsExpandable = false,
        bool IsExpanded = false)
        : FileBrowserRow(FullPath, Name, Depth, IsIgnored, IsHidden, IsLink, Guides);

    /// <summary>A declaration inside the file at <see cref="FullPath"/>. Carries the file's own
    /// ignored and hidden flags so a declaration under a dimmed file is dimmed with it, and opens
    /// when it declares anything itself.</summary>
    /// <param name="SymbolPath">The containment chain that names it within the file, overloads
    /// included — <c>App.AuthService.Login(string)</c>.</param>
    public sealed record Symbol(
        string FullPath,
        string Name,
        int Depth,
        bool IsIgnored,
        bool IsHidden,
        TreeGuides Guides,
        SymbolKind Kind,
        string? ParameterTypes,
        int StartLine,
        string SymbolPath,
        bool IsExpandable = false,
        bool IsExpanded = false)
        : FileBrowserRow(FullPath, Name, Depth, IsIgnored, IsHidden, IsLink: false, Guides)
    {
        public override string RowKey => FullPath + '\n' + SymbolPath;
    }
}
