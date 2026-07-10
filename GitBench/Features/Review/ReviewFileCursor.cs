using GitBench.Features.Commits;
using GitBench.Features.Diff;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// The review loop's cursor over a file list: which file is being read, which are selected, and which
/// is next in the queue (the first unmarked file, in list order). Owns nothing about *where* the files
/// come from — a <c>base..head</c> range or the working tree — so both review surfaces share one
/// implementation of stepping, selecting, scrollspy and mark-and-advance.
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
    private readonly Derived<string?> _queuedFile;

    /// <summary>Raised when a navigation (tree click, j/k, mark-and-advance) wants the stacked diff
    /// list to scroll a file's section into view. Scrollspy updates never raise it.</summary>
    public event Action<string>? ScrollToFileRequested;

    public ReviewFileCursor(Func<IReadOnlyList<FileChange>> files, IReviewedFileTracker marks)
    {
        _files = files;
        _marks = marks;
        _selectedPaths = new Derived<IReadOnlySet<string>>(() => _selection.Value.Set);
        _selectionCursor = new Derived<string?>(() => _selection.Value.Cursor);
        _queuedFile = new Derived<string?>(FirstUnmarked);
    }

    public IReadable<string?> ActiveFile => _activeFile;
    public IReadable<IReadOnlySet<string>> SelectedPaths => _selectedPaths;
    public IReadable<string?> SelectionCursor => _selectionCursor;
    public IReadable<string?> QueuedFile => _queuedFile;

    /// <summary>Adopts a freshly loaded file list: seeds the active file on the first load, and prunes
    /// the active file and the selection of paths the list no longer carries.</summary>
    public void OnFilesLoaded(IReadOnlyList<FileChange> files)
    {
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

    /// <summary>Scrollspy: the stacked diff list reports the file its viewport sits on, so the tree
    /// highlight and the keyboard anchor follow the reading position without echoing a scroll back.
    /// Scrolling within a multi-selection keeps it; scrolling out of one collapses it onto the file
    /// now being read.</summary>
    public void ReportActiveFile(string path)
    {
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

    /// <summary>Marks the queued file and rides to the new head of the queue. The queue is strictly in
    /// list order, independent of where the reviewer scrolled or clicked, so a file unmarked earlier
    /// in the list becomes the queue head again.</summary>
    public void MarkQueuedFileAndAdvance()
    {
        var queued = FirstUnmarked();
        if (queued == null) return;
        _marks.ToggleViewed(queued);
        if (FirstUnmarked() is { } next) ActivateFile(next);
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

    // The head of the review queue: the first file in list order without a mark.
    private string? FirstUnmarked()
    {
        _ = _marks.Revision.Value;
        foreach (var f in _files())
            if (!_marks.IsViewed(f.Path)) return f.Path;
        return null;
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
        _queuedFile.Dispose();
        _selectionCursor.Dispose();
        _selectedPaths.Dispose();
        _selection.Dispose();
        _activeFile.Dispose();
    }
}
