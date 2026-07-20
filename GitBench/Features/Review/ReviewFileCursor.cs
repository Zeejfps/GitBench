using GitBench.Features.Commits;
using GitBench.Features.Diff;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// The review loop's cursor over a file list: which file is being read and which are selected. Owns
/// nothing about *where* the files come from — a <c>base..head</c> range or the working tree — so both
/// review surfaces share one implementation of stepping, selecting and scrollspy.
///
/// The host supplies the current file list and the mark tracker; every derived signal reads through
/// them, so it refreshes on a file-list reload and on every mark toggle.
/// </summary>
internal sealed class ReviewFileCursor : IDisposable
{
    private readonly Func<IReadOnlyList<FileChange>> _files;
    private readonly IReviewedFileTracker _marks;

    private readonly State<string?> _activeFile = new(null);
    private readonly State<ReviewSelection> _selection = new(ReviewSelection.Empty);
    private readonly Derived<IReadOnlySet<string>> _selectedPaths;
    private readonly Derived<string?> _selectionCursor;

    // Set while the selection was deliberately emptied (the tree's cursor moved onto a folder row).
    // The working tree re-pushes its file list on every index op and editor save, so without this the
    // reload below would seed the first file straight back in and steal the folder's highlight.
    private bool _selectionCleared;

    /// <summary>Raised when a navigation (tree click, j/k, mark-and-advance) wants the stacked diff
    /// list to scroll a file's section into view. Scrollspy updates never raise it.</summary>
    public event Action<string>? ScrollToFileRequested;

    public ReviewFileCursor(Func<IReadOnlyList<FileChange>> files, IReviewedFileTracker marks)
    {
        _files = files;
        _marks = marks;
        _selectedPaths = new Derived<IReadOnlySet<string>>(() => _selection.Value.Set);
        _selectionCursor = new Derived<string?>(() => _selection.Value.Cursor);
    }

    public IReadable<string?> ActiveFile => _activeFile;
    public IReadable<IReadOnlySet<string>> SelectedPaths => _selectedPaths;
    public IReadable<string?> SelectionCursor => _selectionCursor;

    /// <summary>Adopts a freshly loaded file list: seeds the active file on the first load, and prunes
    /// the active file and the selection of paths the list no longer carries.</summary>
    public void OnFilesLoaded(IReadOnlyList<FileChange> files)
    {
        if (_selectionCleared)
        {
            var held = _selection.Value;
            _selection.Value = ReviewSelection.Create(held.Paths, held.Anchor, held.Cursor, files);
            return;
        }

        var active = _activeFile.Value;
        if (active == null || IndexOfFile(files, active) < 0)
            active = files.Count > 0 ? files[0].Path : null;
        _activeFile.Value = active;

        var s = _selection.Value;
        var pruned = ReviewSelection.Create(s.Paths, s.Anchor, s.Cursor, files);
        _selection.Value = active != null && !pruned.Contains(active)
            ? ReviewSelection.Single(active, files)
            : pruned;
    }

    /// <summary>Navigates to a file: selects it alone, makes it active, and asks the stacked diff list
    /// to scroll its section into view.</summary>
    public void ActivateFile(string path) => ApplySelection(ReviewSelection.Single(path, _files()), scroll: true);

    /// <summary>Drops the selection and the active file, for the tree's cursor moving onto a folder
    /// row — a folder is not a diff target, so nothing is being read.</summary>
    public void ClearSelection() => ApplySelection(ReviewSelection.Empty, scroll: false);

    /// <summary>
    /// Updates the selection for a tree row gesture. A plain gesture selects just that row; Ctrl/Cmd
    /// toggles it; Shift extends the range from the anchor over <paramref name="visiblePaths"/> — the
    /// file rows the tree currently shows, so a file hidden under a collapsed folder is never swept
    /// in. The anchor moves on plain/toggle gestures and stays put on a shift-extend.
    /// </summary>
    public void SelectFile(string path, InputModifiers modifiers, IReadOnlyList<string> visiblePaths)
    {
        var files = _files();
        if (files.Count == 0) return;

        var current = _selection.Value;
        var shift = (modifiers & InputModifiers.Shift) != 0;
        var toggle = (modifiers & (InputModifiers.Control | InputModifiers.Super)) != 0;

        if (shift && current.Anchor is { } anchor && IndexOf(visiblePaths, anchor) >= 0)
        {
            ApplySelection(
                ReviewSelection.Create(Range(visiblePaths, anchor, path), anchor, path, files),
                scroll: false);
            return;
        }

        if (toggle)
        {
            var next = new List<string>(current.Count + 1);
            foreach (var p in current.Paths)
                if (p != path) next.Add(p);
            if (next.Count == current.Count) next.Add(path);
            ApplySelection(ReviewSelection.Create(next, path, path, files), scroll: false);
            return;
        }

        ApplySelection(ReviewSelection.Single(path, files), scroll: true);
    }

    /// <summary>Selects every file row the tree currently shows (Ctrl/Cmd+A).</summary>
    public void SelectAllFiles(IReadOnlyList<string> visiblePaths)
    {
        var files = _files();
        if (files.Count == 0 || visiblePaths.Count == 0) return;
        ApplySelection(
            ReviewSelection.Create(visiblePaths, visiblePaths[0], visiblePaths[^1], files),
            scroll: false);
    }

    /// <summary>The stacked diff list reports the file a click landed on, so the tree highlight follows
    /// without echoing a scroll back. Clicking inside a multi-selection keeps it; clicking outside one
    /// collapses it onto that file. Scrolling never calls this — reading is not selecting.</summary>
    public void ReportActiveFile(string path)
    {
        _selectionCleared = false;
        _activeFile.Value = path;
        if (!_selection.Value.Contains(path))
            _selection.Value = ReviewSelection.Single(path, _files());
    }

    public void NextFile() => StepFile(+1);
    public void PrevFile() => StepFile(-1);

    /// <summary>Flips the active file's mark — the 'v' key's reversible toggle.</summary>
    public void ToggleActiveFileMarked()
    {
        if (_activeFile.Value is { } active) _marks.ToggleViewed(active);
    }

    /// <summary>The files a row action applies to: the whole selection when the row is part of it, else
    /// just that row (right-clicking elsewhere never silently retargets the selection).</summary>
    public IReadOnlyList<string> ResolveTargetPaths(string path)
    {
        var selection = _selection.Value;
        return selection.Contains(path) ? selection.Paths : [path];
    }

    /// <summary>How many of the given files carry a mark. Reads the tracker's revision so callers that
    /// wrap this in a <see cref="Derived{T}"/> refresh on every toggle.</summary>
    public int CountMarked(IReadOnlyList<FileChange> files)
    {
        _ = _marks.Revision.Value;
        var marked = 0;
        foreach (var f in files)
            if (_marks.IsViewed(f.Path)) marked++;
        return marked;
    }

    private void StepFile(int delta)
    {
        var files = _files();
        if (files.Count == 0) return;
        var active = _activeFile.Value;
        int next;
        if (active == null)
        {
            next = delta > 0 ? 0 : files.Count - 1;
        }
        else
        {
            var index = IndexOfFile(files, active);
            next = index < 0 ? 0 : Math.Clamp(index + delta, 0, files.Count - 1);
        }
        ActivateFile(files[next].Path);
    }

    // Publishes a new selection and re-focuses the diff surface on its lead. Scrolling is implicit
    // whenever the lead moves; `scroll` additionally forces it for a deliberate navigation onto the
    // file already active (a click on the current row still recenters it).
    private void ApplySelection(ReviewSelection next, bool scroll)
    {
        _selection.Value = next;
        var lead = next.Lead;
        _selectionCleared = lead == null;
        if (lead == null)
        {
            _activeFile.Value = null;
            return;
        }
        var moved = lead != _activeFile.Value;
        _activeFile.Value = lead;
        if (scroll || moved) ScrollToFileRequested?.Invoke(lead);
    }

    private static IReadOnlyList<string> Range(IReadOnlyList<string> visible, string from, string to)
    {
        var a = IndexOf(visible, from);
        var b = IndexOf(visible, to);
        if (a < 0 || b < 0) return [to];
        var (lo, hi) = a <= b ? (a, b) : (b, a);
        var range = new List<string>(hi - lo + 1);
        for (var i = lo; i <= hi; i++) range.Add(visible[i]);
        return range;
    }

    private static int IndexOf(IReadOnlyList<string> paths, string path)
    {
        for (var i = 0; i < paths.Count; i++)
            if (paths[i] == path) return i;
        return -1;
    }

    private static int IndexOfFile(IReadOnlyList<FileChange> files, string path)
    {
        for (var i = 0; i < files.Count; i++)
            if (files[i].Path == path) return i;
        return -1;
    }

    public void Dispose()
    {
        _selectionCursor.Dispose();
        _selectedPaths.Dispose();
        _selection.Dispose();
        _activeFile.Dispose();
    }
}
