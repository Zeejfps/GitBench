using GitBench.Features.Diff;
using GitBench.Features.Repos;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// What a file's header mark means on a review surface, so the shared widgets label it correctly.
/// <see cref="Viewed"/> is the branch review's ephemeral "I've read this"; <see cref="Staged"/> is
/// the working-tree review's index state — checking a file stages it.
/// </summary>
internal enum ReviewMarkKind
{
    Viewed,
    Staged,
}

/// <summary>
/// The model behind a stacked review surface (<see cref="ReviewDiffPanel"/> + the file tree +
/// <see cref="ReviewKeyController"/>), independent of where its files come from.
/// <see cref="ReviewWindowViewModel"/> implements it over a <c>base..head</c> range;
/// <see cref="LocalChanges.WorkingTreeReviewViewModel"/> over the working tree, where a file's mark
/// is its staged state.
/// </summary>
internal interface IReviewSurfaceModel
{
    /// <summary>What checking a file's header box means here — drives the labels only.</summary>
    ReviewMarkKind MarkKind { get; }

    /// <summary>Per-file marks, also provided into the subtree so the file tree paints them.</summary>
    IReviewedFileTracker ReviewedFiles { get; }

    /// <summary>The selected file — the lead of the selection, moved by the tree, j/k, or a click on a
    /// card's header. Scrolling past a file does not select it.</summary>
    IReadable<string?> ActiveFile { get; }

    /// <summary>The tree's selected files, and the row arrow keys step on from.</summary>
    IReadable<IReadOnlySet<string>> SelectedPaths { get; }
    IReadable<string?> SelectionCursor { get; }

    /// <summary>Progress, for the header.</summary>
    IReadable<ReviewHud> Hud { get; }

    IReadable<bool> CheatsheetOpen { get; }

    /// <summary>Raised when a navigation wants the stacked list to scroll a file into view.</summary>
    event Action<string>? ScrollToFileRequested;

    bool IsFileViewed(string path);

    /// <summary>Whether the file's mark is only half-true — the working-tree review's partially staged
    /// file, which has staged content and further unstaged edits on top. Always false where a mark is
    /// binary. Drives the indeterminate checkbox.</summary>
    bool IsFilePartiallyMarked(string path) => false;

    void ToggleFileViewed(string path);
    void ToggleActiveFileViewed();

    /// <summary>The stacked list reports the file a click landed on, so the tree highlight follows it.
    /// Never raised by scrolling — the reader's position is not a selection.</summary>
    void ReportActiveFile(string path);

    void ActivateFile(string path);
    void SelectFile(string path, InputModifiers modifiers, IReadOnlyList<string> visiblePaths);
    void SelectAllFiles(IReadOnlyList<string> visiblePaths);
    void NextFile();
    void PrevFile();

    void ToggleCheatsheet();
    void CloseCheatsheet();

    /// <summary>A file's right-click menu, on a stacked diff card or a tree row alike — a file is a
    /// file, and folding is not one of its actions.</summary>
    IReadOnlyList<RepoBarContextMenu.Item> BuildFileContextMenuItems(string path);

    /// <summary>A tree folder row's menu: the per-file actions over everything beneath it, plus
    /// Expand/Collapse All scoped to that folder's subtree.</summary>
    IReadOnlyList<RepoBarContextMenu.Item> BuildTreeFolderContextMenuItems(string folderPath, IReadOnlyList<string> paths);

    /// <summary>The menu for a right-click below the last row: no file to act on, so the commands that
    /// take the tree as a whole — including Expand/Collapse All over everything.</summary>
    IReadOnlyList<RepoBarContextMenu.Item> BuildTreeEmptyContextMenuItems();
}
