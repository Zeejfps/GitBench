using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.Markdown;
using GitBench.Git;
using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>Which body the preview shows. Text is the common case; a picture and a sentence are the
/// two things a patch view cannot render.</summary>
internal enum FileBrowserBodyKind { Text, Markdown, Image, Placeholder }

/// <summary>Why the preview is being pointed at a file, which decides how much work it can skip.</summary>
internal enum PreviewSync
{
    /// <summary>Follow the tabs. A file already on screen needs nothing.</summary>
    Follow,

    /// <summary>Read the file again, and publish only if it moved.</summary>
    Reread,

    /// <summary>Read the file again and publish whatever comes back.</summary>
    Rebuild,
}

/// <summary>
/// One repository's file browser: the rows on screen, the cursor, the open tabs, and the operations
/// that move any of them. Owns a <see cref="FileBrowserTree"/> and is the only thing that touches it.
/// </summary>
/// <remarks>
/// <para>
/// Every tree operation reads the disk, so none of them runs on the UI thread. They queue onto one
/// serial task chain — a lane, not a pool — because the tree is a cache and an expanded set, and two
/// listings racing each other over it would interleave. The chain is only ever extended from the UI
/// thread, which is what makes the bare field safe. Results come back through the dispatcher.
/// </para>
/// <para>
/// A published row list is never mutated afterwards, so handing one across the two threads is a
/// handover rather than sharing.
/// </para>
/// <para>
/// What is on screen is the active tab, not the cursor. The two normally agree — selecting a file
/// opens it — but they answer different questions: the cursor is where the keyboard is, and a
/// directory or a file the tree has stopped listing can hold it while a file stays open beside it.
/// </para>
/// </remarks>
internal sealed class FileBrowserViewModel : IFileNavigator, IDisposable
{
    private readonly string _root;
    private readonly IFileSystemReader _files;
    private readonly Func<IReadOnlyList<string>, IReadOnlySet<string>> _ignored;
    private readonly ISymbolExtractor _extractor;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<FileBrowserUiState> _persist;
    private readonly IRepoDocuments _documents;
    private readonly Action<IReadOnlyList<string>, Action> _confirmDiscard;
    private readonly Action<IReadOnlyList<string>, Action> _confirmReload;

    private readonly State<IReadOnlyList<FileBrowserRow>> _rows = new([]);
    private readonly State<string?> _cursor = new(null);
    private readonly State<bool> _showHidden = new(true);
    private readonly State<FilePreview> _preview = new(FilePreview.None.Instance);
    private readonly State<bool> _renderMarkdown = new(true);
    private readonly State<string?> _breadcrumb = new(null);
    private readonly State<FoldState> _folds = new(FoldState.Open(string.Empty));

    private readonly FileBrowserTabs _tabs;
    private readonly FileSearchViewModel _search;
    private readonly IDisposable _searchRetarget;
    private readonly FileFinderViewModel _finder;

    private string[] _expanded = [];

    /// <summary>The tree's own rows, kept while the rail is showing something else. What
    /// <see cref="Rows"/> holds is these or the finder's results; what the tree knows is only ever
    /// these.</summary>
    private IReadOnlyList<FileBrowserRow> _treeRows = [];

    private FileBrowserTree? _tree;
    private Task _lane = Task.CompletedTask;
    private bool _disposed;
    private bool _restoring;

    private CancellationTokenSource? _previewCancel;
    private int _previewGeneration;
    private string? _previewPath;
    private (string Path, int Line)? _pendingReveal;
    private int _topVisibleLine;

    public FileBrowserViewModel(
        Repo repo,
        IFileSystemReader files,
        Func<IReadOnlyList<string>, IReadOnlySet<string>> ignored,
        Func<IReadOnlyList<string>> listFiles,
        ISymbolExtractor extractor,
        IUiDispatcher dispatcher,
        FileBrowserUiState restored,
        Action<FileBrowserUiState> persist,
        IRepoDocuments documents,
        Action<IReadOnlyList<string>, Action> confirmDiscard,
        Action<IReadOnlyList<string>, Action> confirmReload)
    {
        _root = PathKey.Normalize(repo.Path);
        RepoId = repo.Id;
        _files = files;
        _ignored = ignored;
        _extractor = extractor;
        _dispatcher = dispatcher;
        _persist = persist;
        _documents = documents;
        _confirmDiscard = confirmDiscard;
        _confirmReload = confirmReload;
        _tabs = new FileBrowserTabs(documents.HasUnsavedEdits);

        _search = new FileSearchViewModel(() => _preview.Value, () => _topVisibleLine);
        _searchRetarget = _preview.Subscribe(_ => _search.Retarget());

        _finder = new FileFinderViewModel(listFiles, dispatcher);
        _finder.Changed += PublishRows;

        _showHidden.Value = restored.ShowHidden;
        _renderMarkdown.Value = restored.RenderMarkdown;
        Restore(restored);

        var expanded = restored.Expanded.Select(Restored).OfType<string>().ToArray();
        var showHidden = restored.ShowHidden;
        Queue(tree =>
        {
            tree.SetShowHidden(showHidden);
            tree.RestoreExpanded(expanded);
        });
    }

    /// <summary>Reopens the tabs and the cursor the reader left, without any of it counting as
    /// somewhere they have navigated: a fresh session's history starts empty.</summary>
    /// <remarks>
    /// Only the tabs decide what is open. The cursor is where the keyboard was — after closing the
    /// last tab it is still on that file's row — and a restore that opened it would hand back the
    /// tab the reader just closed, every launch. A tab whose file has gone since is dropped rather
    /// than reopened onto nothing.
    /// </remarks>
    private void Restore(FileBrowserUiState state)
    {
        _restoring = true;
        try
        {
            foreach (var relative in state.Tabs)
                if (RestoredFile(relative) is { } path) _tabs.Open(path, pinned: true);

            if (RestoredFile(state.ActiveTab) is { } active && _tabs.Find(active) is not null)
                Show(active, pinned: true, line: null);
            else if (_tabs.Items.Count > 0)
                Show(_tabs.Items[^1].Path, pinned: true, line: null);

            if (Restored(state.Cursor) is { } cursor) _cursor.Value = cursor;
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>The working tree this browser is rooted at.</summary>
    public string RootPath => _root;

    /// <summary>Which repository's working tree that is — for the callers that have to say what
    /// moved when they write to it.</summary>
    public Guid RepoId { get; }

    public IReadable<IReadOnlyList<FileBrowserRow>> Rows => _rows;

    /// <summary>The row the keyboard is on, as a <see cref="FileBrowserRow.RowKey"/>, or null when
    /// nothing is selected.</summary>
    public IReadable<string?> Cursor => _cursor;

    public IReadable<bool> ShowHidden => _showHidden;

    /// <summary>What the pane draws beside the tree: the active tab's file, or why it cannot be
    /// drawn.</summary>
    public IReadable<FilePreview> Preview => _preview;

    public IReadable<bool> RenderMarkdown => _renderMarkdown;

    public MarkdownRender? MarkdownPreview =>
        (_preview.Value as FilePreview.Text)?.Markdown;

    /// <summary>The declarations in the previewed file, or null when there are none to offer — an
    /// unsupported language, a file over the parser's cap, no grammar loaded.</summary>
    public FileOutline? Outline =>
        (_preview.Value as FilePreview.Text)?.Outline;

    /// <summary>The files open in the strip above the preview, in the order they were opened.</summary>
    public ObservableList<FileBrowserTab> Tabs => _tabs.Items;

    /// <summary>The tab the preview is showing, or null when nothing is open.</summary>
    public IReadable<FileBrowserTab?> ActiveTab => _tabs.Active;

    /// <summary>A file became the one this browser is showing. Silent while the session's tabs are
    /// being reopened: restoring what the reader left is not them going anywhere.</summary>
    public event Action<FileBrowserMove>? FileShown;

    /// <summary>The last open file was closed, so this browser is showing nothing.</summary>
    public event Action? AllFilesClosed;

    /// <summary>This repository's files open for editing.</summary>
    public IRepoDocuments Documents => _documents;

    /// <summary>Which declarations are folded shut in the previewed file. Per file, UI thread only,
    /// and deliberately not persisted — a fold is a reading position, not a preference.</summary>
    public IReadable<FoldState> Folds => _folds;

    /// <summary>Folds or unfolds one declaration of the previewed file, by the id its row carries.</summary>
    public void ToggleFold(string id)
    {
        if (_disposed || _previewPath is not { } path) return;
        _folds.Value = (_folds.Value.Path == path ? _folds.Value : FoldState.Open(path)).Toggled(id);
    }

    /// <summary>What the preview draws the open file as. Decided here rather than by the pane, so
    /// everything that has to know whether there is text on screen agrees on the answer.</summary>
    public FileBrowserBodyKind BodyKind => _preview.Value switch
    {
        FilePreview.Text { Markdown: not null } when _renderMarkdown.Value => FileBrowserBodyKind.Markdown,
        FilePreview.Text => FileBrowserBodyKind.Text,
        FilePreview.Image => FileBrowserBodyKind.Image,
        _ => FileBrowserBodyKind.Placeholder,
    };

    /// <summary>Whether there is something on screen to find anything in. A picture is not, and
    /// neither is a rendered markdown document: find highlights a file's lines, and what the reader
    /// is looking at there is paragraphs.</summary>
    public bool CanSearch => BodyKind == FileBrowserBodyKind.Text;

    /// <summary>Find-in-file over whatever the preview is showing. Lives here rather than in the
    /// bar, so a query survives the bar being closed and the pane being switched away from.</summary>
    public FileSearchViewModel Search => _search;

    /// <summary>Find-a-file over the whole working tree. While it is answering something,
    /// <see cref="Rows"/> is its results rather than the tree — the rail shows one list, and the
    /// keyboard, the cursor and the preview follow whichever it is.</summary>
    public FileFinderViewModel Finder => _finder;

    /// <summary>Which of the two lists <see cref="Rows"/> is. A result is a taller row than a tree
    /// entry — it carries the directory it was found in under the name — so whoever draws the rows
    /// has to be told, rather than guessing it from one of them.</summary>
    public bool IsShowingResults => _finder.IsFiltering;

    /// <summary>The declaration the reader is currently inside, as a dotted containment path, or
    /// null when the top of the viewport is inside none. Follows the scroll, which is what makes it
    /// worth having: it answers "where am I" for a file too long to hold in your head.</summary>
    public IReadable<string?> Breadcrumb => _breadcrumb;

    /// <summary>Told by whoever is drawing the previewed file which line is at the top of it.</summary>
    public void SetTopVisibleLine(int line)
    {
        if (_disposed) return;
        _topVisibleLine = line;
        // The tab keeps it, so coming back to this file comes back to this line rather than to
        // the top of it.
        if (_tabs.Active.Value is { } tab && _previewPath is { } path && PathKey.Comparer.Equals(tab.Path, path))
            tab.TopLine = line;
        UpdateBreadcrumb();
    }

    private void UpdateBreadcrumb() =>
        _breadcrumb.Value = _topVisibleLine < 1 ? null : Outline?.DeclarationPathAt(_topVisibleLine);

    /// <summary>Asks whoever is drawing the previewed file to bring a line into view. An event
    /// rather than a state because a jump is an occurrence: jumping twice to the same line has to
    /// scroll twice.</summary>
    public event Action<int>? LineRevealRequested;

    /// <summary>The one way to move the reader within the previewed file, whatever asks — the tree's
    /// declaration rows, the tab strip returning to where they left off, an unfold-and-jump later. A
    /// file that is still being read holds the line until its text lands; anything that is not a
    /// file's text, or that the reader has moved off before the read finished, drops it.</summary>
    public void NavigateToLine(int line)
    {
        if (_disposed || line < 1) return;

        Unfold(line);

        if (_preview.Value is FilePreview.Text)
        {
            _pendingReveal = null;
            LineRevealRequested?.Invoke(line);
            return;
        }

        _pendingReveal = _previewPath is { } path ? (path, line) : null;
    }

    /// <summary>Opens a file the reader asked for by name rather than by pointing at it — a
    /// definition jump. Pinned, because the trail of files a jump left behind is the thing the back
    /// button is for.</summary>
    public void NavigateTo(string absolutePath, int line)
    {
        if (_disposed) return;
        Travel(absolutePath, rowKey: null, line, pinned: true);
    }

    /// <summary>Puts the browser back on a place the content panel's trail kept — the file, the row
    /// the tree was on inside it, and the line that was being read.</summary>
    public void Restore(FileBrowserPlace place)
    {
        if (_disposed) return;
        Travel(place.AbsolutePath, place.RowKey, place.Line, pinned: false);
    }

    /// <summary>Shows a tab's file. Idempotent, so the strip can hand back the tab already on
    /// screen without it counting as a move.</summary>
    public void ActivateTab(FileBrowserTab tab)
    {
        if (_disposed || _tabs.Items.IndexOf(tab) < 0) return;
        Travel(tab.Path, rowKey: null, line: null, pinned: false);
    }

    public void CloseTab(FileBrowserTab tab)
    {
        if (_disposed) return;
        Closing([tab], () =>
        {
            var wasActive = ReferenceEquals(_tabs.Active.Value, tab);
            Release(tab);
            _tabs.Close(tab);
            if (wasActive) FollowActiveTab();
            Persist();
        });
    }

    public void CloseOtherTabs(FileBrowserTab keep)
    {
        if (_disposed) return;
        var closing = _tabs.Items.Where(tab => !ReferenceEquals(tab, keep)).ToArray();
        Closing(closing, () =>
        {
            foreach (var tab in closing) Release(tab);
            _tabs.CloseOthers(keep);
            FollowActiveTab();
            Persist();
        });
    }

    public void CloseAllTabs()
    {
        if (_disposed) return;
        var closing = _tabs.Items.ToArray();
        Closing(closing, () =>
        {
            foreach (var tab in closing) Release(tab);
            _tabs.CloseAll();
            FollowActiveTab();
            Persist();
        });
    }

    /// <summary>Runs <paramref name="close"/>, first asking once about whatever it would
    /// lose.</summary>
    private void Closing(IReadOnlyList<FileBrowserTab> closing, Action close)
    {
        var unsaved = closing
            .Where(tab => _documents.HasUnsavedEdits(tab.Path))
            .Select(tab => PathLabel(tab.Path))
            .ToArray();

        if (unsaved.Length == 0)
        {
            close();
            return;
        }

        _confirmDiscard(unsaved, () => { if (!_disposed) close(); });
    }

    private void Release(FileBrowserTab tab) => _documents.Close(tab.Path);

    /// <summary>How a file is named in this browser: repo-relative inside the working tree, its
    /// whole path outside it.</summary>
    public string PathLabel(string absolutePath)
    {
        var path = PathKey.Normalize(absolutePath);
        return ToRelative(path) ?? path.Replace('\\', '/');
    }

    public string TitleFor(FilePreview preview)
    {
        var path = preview switch
        {
            FilePreview.Loading loading => loading.Path,
            FilePreview.Text text => text.Path,
            FilePreview.Image image => image.Path,
            FilePreview.Unavailable unavailable => unavailable.Path,
            _ => null,
        };

        return path is null ? string.Empty : PathLabel(path);
    }

    /// <summary>Where the reader is, for the content panel's trail to come back to. Null when
    /// nothing is open, which is not a place: there is nothing to return to.</summary>
    public FileBrowserPlace? Place => Here();

    private FileBrowserPlace? Here()
    {
        if (_tabs.Active.Value is not { } tab) return null;
        var cursor = _cursor.Value is { } key && PathKey.Comparer.Equals(FileOf(key) ?? key, tab.Path)
            ? key
            : null;
        return new FileBrowserPlace(tab.Path, cursor, _topVisibleLine);
    }

    /// <summary>
    /// The whole gesture of going to a file: the tab and the preview first, then the tree catching
    /// up to it and the cursor landing on the row.
    /// </summary>
    /// <remarks>
    /// The preview does not wait for the listing. Opening a directory chain can take a disk read per
    /// level, and a jump that showed nothing until the tree agreed would read as the jump having
    /// missed. The cursor does wait — moving it to a row that is not there yet would move it
    /// somewhere else.
    /// </remarks>
    private void Travel(string absolutePath, string? rowKey, int? line, bool pinned)
    {
        var path = PathKey.Normalize(absolutePath);
        Show(path, pinned, line);

        if (ToRelative(path) is null)
        {
            // Outside the working tree: there is no row to select, so the tree holds nothing rather
            // than pointing at the file the reader has left.
            _cursor.Value = null;
            Persist();
            return;
        }

        Queue(tree => tree.Reveal(path), () => Land(path, rowKey));
    }

    private void Land(string path, string? rowKey)
    {
        if (_disposed) return;
        _cursor.Value = rowKey is not null && HasRow(rowKey) ? rowKey : HasRow(path) ? path : null;
        Persist();
    }

    /// <summary>
    /// Points the tabs — and through them the preview — at a file. The one place the preview moves
    /// from, so nothing can move it without the content panel's trail hearing about it. Answers
    /// whether the strip now shows something else.
    /// </summary>
    private bool Show(string path, bool pinned, int? line)
    {
        var leaving = Here();
        var previous = _tabs.Active.Value;
        var openCount = _tabs.Items.Count;
        var tab = _tabs.Open(path, pinned);
        var switched = !ReferenceEquals(previous, tab);

        SyncPreview(PreviewSync.Follow);

        if (line is { } target) NavigateToLine(target);
        else if (switched && tab.TopLine > 0) NavigateToLine(tab.TopLine);

        // Asking again for the file already on screen is not somewhere new, even though the panel
        // may still have to swing over to it from another tab.
        var moved = switched || (line is { } asked && leaving is not null && asked != leaving.Line);
        if (!_restoring) FileShown?.Invoke(new FileBrowserMove(leaving, moved));

        return switched || _tabs.Items.Count != openCount;
    }

    /// <summary>Puts the cursor and the preview back on whatever the tabs are showing now — after a
    /// close, which is the one thing that moves the preview without the reader naming where to.</summary>
    private void FollowActiveTab()
    {
        SyncPreview(PreviewSync.Follow);
        if (_tabs.Active.Value is not { } tab)
        {
            AllFilesClosed?.Invoke();
            return;
        }

        if (HasRow(tab.Path)) _cursor.Value = tab.Path;
        if (tab.TopLine > 0) NavigateToLine(tab.TopLine);
    }

    private bool HasRow(string rowKey)
    {
        foreach (var row in _rows.Value)
            if (PathKey.Comparer.Equals(row.RowKey, rowKey)) return true;
        return false;
    }

    /// <summary>The file a row is of — its own, or the one a declaration lives in — or null for a
    /// row that is not a way into a file.</summary>
    private string? FileOf(string rowKey)
    {
        foreach (var row in _rows.Value)
        {
            if (!PathKey.Comparer.Equals(row.RowKey, rowKey)) continue;
            return row switch
            {
                FileBrowserRow.File file => file.FullPath,
                FileBrowserRow.Symbol symbol => symbol.FullPath,
                _ => null,
            };
        }
        return null;
    }

    /// <summary>The tail of the serial lane. Held so a test can wait for the disk work it started
    /// rather than sleeping for it; nothing in the application awaits it.</summary>
    internal Task Pending => _lane;

    public void SetRenderMarkdown(bool render)
    {
        if (_renderMarkdown.Value == render) return;
        _renderMarkdown.Value = render;
        // The rendered document has no lines to highlight, so the bar would stand there over
        // nothing until the reader switched back.
        if (BodyKind == FileBrowserBodyKind.Markdown) _search.Close();
        Persist();
    }

    public void SetShowHidden(bool show)
    {
        if (_showHidden.Value == show) return;
        _showHidden.Value = show;
        Queue(tree => tree.SetShowHidden(show));
    }

    /// <summary>
    /// Puts a path that has just been created on screen: the listing is re-read, the directories
    /// above it are opened, and the cursor lands on it. A new file is opened too — creating one is
    /// asking to write in it — while a new directory is only expanded, since there is nothing in it
    /// to read.
    /// </summary>
    public void Created(string absolutePath, bool isDirectory)
    {
        if (_disposed) return;

        var path = PathKey.Normalize(absolutePath);
        if (ToRelative(path) is null) return;

        Queue(
            tree =>
            {
                tree.Refresh();
                tree.Reveal(path);
                if (isDirectory) tree.Expand(path);
            },
            () =>
            {
                if (_disposed) return;
                if (!isDirectory) Show(path, pinned: true, line: null);
                Land(path, rowKey: null);
            });
    }

    /// <summary>
    /// Drops a path that is no longer on disk: whatever it had open is closed — without asking about
    /// unsaved edits, because there is nothing left to save them into — and the listing is re-read.
    /// A directory takes everything under it with it.
    /// </summary>
    public void Deleted(string absolutePath)
    {
        if (_disposed) return;

        var path = PathKey.Normalize(absolutePath);
        var closing = _tabs.Items.Where(tab => IsAtOrUnder(tab.Path, path)).ToArray();
        if (closing.Length > 0)
        {
            var wasActive = _tabs.Active.Value is { } active && IsAtOrUnder(active.Path, path);
            foreach (var tab in closing)
            {
                Release(tab);
                _tabs.Close(tab);
            }
            if (wasActive) FollowActiveTab();
        }

        if (_cursor.Value is { } cursor && IsAtOrUnder(cursor.Split('\n')[0], path)) _cursor.Value = null;

        Queue(tree => tree.Refresh());
        Persist();
    }

    /// <summary>Whether a path is the given one or sits beneath it.</summary>
    private static bool IsAtOrUnder(string path, string root)
    {
        if (PathKey.Comparer.Equals(path, root)) return true;
        var prefix = root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length
            && PathKey.Comparer.Equals(path[..prefix.Length], prefix);
    }

    /// <summary>Re-lists what is open and re-reads the previewed file. Bounded by what the reader
    /// opened, not by what is on disk — this runs twice a minute at idle on every platform, so it
    /// re-reads silently and publishes nothing when the bytes have not moved.</summary>
    public void Invalidate()
    {
        Queue(tree => tree.Refresh());
        SyncPreview(PreviewSync.Reread);
        Reconcile();
    }

    /// <summary>Looks at the file behind every open document and asks about the ones that moved
    /// under unsaved edits.</summary>
    private void Reconcile()
    {
        var open = _documents.OpenDocuments();
        if (open.Count == 0) return;

        var dispatcher = _dispatcher;
        Task.Run(() =>
        {
            var looked = new (string Path, FileStamp? Expected, FileStamp? Found)[open.Count];
            for (var i = 0; i < open.Count; i++)
                looked[i] = (open[i].Path, open[i].KnownOnDisk, FileStamp.Of(open[i].Path));

            dispatcher.Post(() => Settle(looked));
        });
    }

    private void Settle(IReadOnlyList<(string Path, FileStamp? Expected, FileStamp? Found)> looked)
    {
        if (_disposed) return;

        var diverged = new List<string>();
        foreach (var (path, expected, found) in looked)
            if (_documents.Reconcile(path, expected, found) == DocumentReconciliation.Diverged)
                diverged.Add(path);

        if (diverged.Count == 0) return;
        _confirmReload(diverged.Select(PathLabel).ToArray(), () => Reload(diverged));
    }

    private void Reload(IReadOnlyList<string> paths)
    {
        if (_disposed) return;

        var showing = false;
        foreach (var path in paths)
        {
            _documents.Close(path);
            showing |= PathKey.Comparer.Equals(path, _previewPath);
        }

        if (showing) SyncPreview(PreviewSync.Rebuild);
    }

    /// <summary>Moves the cursor, and opens the file it landed on. Transiently: a cursor sweeping
    /// down a directory is looking, not opening, and the tab it borrows is handed to the next
    /// file rather than left behind.</summary>
    public void SetCursor(string rowKey)
    {
        if (_disposed) return;

        var opened = FileOf(rowKey) is { } file && Show(file, pinned: false, line: null);
        if (_cursor.Value == rowKey && !opened) return;

        _cursor.Value = rowKey;
        Persist();
    }

    public void Toggle(FileBrowserRow.Directory row)
    {
        SetCursor(row.RowKey);
        var path = row.FullPath;
        Queue(tree => tree.Toggle(path));
    }

    /// <summary>Opens or closes a file's declarations. Only a file the parser has a grammar for has
    /// any, so anything else is left alone rather than opening onto nothing.</summary>
    public void ToggleFile(FileBrowserRow.File row)
    {
        if (!row.IsExpandable) return;
        SetCursor(row.RowKey);
        var path = row.FullPath;
        Queue(tree => tree.Toggle(path));
    }

    /// <summary>Opens or closes one declaration's own declarations. Nothing to open is left alone,
    /// so a method's row does not pretend to a chevron.</summary>
    public void ToggleSymbol(FileBrowserRow.Symbol row)
    {
        if (!row.IsExpandable) return;
        SetCursor(row.RowKey);
        var key = row.RowKey;
        Queue(tree => tree.ToggleSymbol(key));
    }

    /// <summary>Selects a declaration: shows the file it lives in, then jumps to it. The two are one
    /// gesture, and the jump waits for the read when the file was not already on screen.</summary>
    public void SelectSymbol(FileBrowserRow.Symbol row)
    {
        if (_disposed) return;

        var opened = Show(row.FullPath, pinned: false, line: row.StartLine);
        if (_cursor.Value == row.RowKey && !opened) return;

        _cursor.Value = row.RowKey;
        Persist();
    }

    /// <summary>Enter, or a double-click: a directory opens or closes, a declaration is jumped to, a
    /// file is opened for good — the tab it was being previewed in stops being borrowed, which is
    /// the difference between looking at a file and working in it. Handing a file to the OS is a
    /// menu item, never a gesture.</summary>
    public void Activate(FileBrowserRow row)
    {
        if (_finder.IsFiltering)
        {
            OpenFound(row.FullPath);
            return;
        }

        switch (row)
        {
            case FileBrowserRow.Directory directory: Toggle(directory); break;
            case FileBrowserRow.Symbol symbol: SelectSymbol(symbol); Pin(symbol.FullPath); break;
            default: SetCursor(row.RowKey); Pin(row.FullPath); break;
        }
    }

    /// <summary>
    /// Enter in the find field: opens the result the cursor is on, or the best one when the reader
    /// typed and pressed Enter without ever leaving the field.
    /// </summary>
    public void ActivateBestMatch()
    {
        if (_disposed || !_finder.IsFiltering) return;

        var rows = _rows.Value;
        if (rows.Count == 0) return;

        var index = IndexOfCursor(rows);
        OpenFound(rows[index < 0 ? 0 : index].FullPath);
    }

    /// <summary>
    /// Takes the reader to a file they found by name: the search is over, and the tree opens onto
    /// where the file lives so that closing the finder leaves them somewhere rather than nowhere.
    /// </summary>
    private void OpenFound(string absolutePath)
    {
        _finder.Close();
        Travel(absolutePath, rowKey: null, line: null, pinned: true);
    }

    private void Pin(string path)
    {
        if (_tabs.Find(path) is not { } tab || !tab.Transient.Value) return;
        tab.Pin();
        Persist();
    }

    public void MoveCursor(int delta)
    {
        var rows = _rows.Value;
        if (rows.Count == 0) return;

        var current = IndexOfCursor(rows);
        var next = current < 0
            ? (delta > 0 ? 0 : rows.Count - 1)
            : Math.Clamp(current + delta, 0, rows.Count - 1);
        SetCursor(rows[next].RowKey);
    }

    /// <summary>Right arrow: open a closed row, or step into an open one.</summary>
    public void ExpandOrDescend()
    {
        var rows = _rows.Value;
        var index = IndexOfCursor(rows);
        if (index < 0) return;

        switch (rows[index])
        {
            case FileBrowserRow.Directory { IsExpanded: false } closed: Toggle(closed); return;
            case FileBrowserRow.File { IsExpandable: true, IsExpanded: false } closed: ToggleFile(closed); return;
            case FileBrowserRow.Symbol { IsExpandable: true, IsExpanded: false } closed: ToggleSymbol(closed); return;
            case FileBrowserRow.Directory
                or FileBrowserRow.File { IsExpandable: true }
                or FileBrowserRow.Symbol { IsExpandable: true }: break;
            default: return;
        }

        if (index + 1 < rows.Count && rows[index + 1].Depth > rows[index].Depth)
            SetCursor(rows[index + 1].RowKey);
    }

    /// <summary>Left arrow: close an open directory, or step out to the parent.</summary>
    public void CollapseOrAscend()
    {
        var rows = _rows.Value;
        var index = IndexOfCursor(rows);
        if (index < 0) return;

        switch (rows[index])
        {
            case FileBrowserRow.Directory { IsExpanded: true } open: Toggle(open); return;
            case FileBrowserRow.File { IsExpanded: true } open: ToggleFile(open); return;
            case FileBrowserRow.Symbol { IsExpanded: true } open: ToggleSymbol(open); return;
        }

        var depth = rows[index].Depth;
        for (var i = index - 1; i >= 0; i--)
        {
            if (rows[i].Depth >= depth) continue;
            SetCursor(rows[i].RowKey);
            return;
        }
    }

    public int IndexOfCursor(IReadOnlyList<FileBrowserRow> rows)
    {
        if (_cursor.Value is not { } cursor) return -1;
        for (var i = 0; i < rows.Count; i++)
            if (PathKey.Comparer.Equals(rows[i].RowKey, cursor)) return i;
        return -1;
    }

    private void Queue(Action<FileBrowserTree> work, Action? then = null)
    {
        if (_disposed) return;

        _lane = _lane.ContinueWith(
            _ =>
            {
                if (_disposed) return;

                IReadOnlyList<FileBrowserRow> rows;
                string[] expanded;
                bool showHidden;
                try
                {
                    var tree = _tree ??= new FileBrowserTree(
                        _files, _ignored, _root,
                        (path, ct) => FileContentLoader.OutlineOf(path, _extractor, ct));
                    work(tree);
                    rows = tree.Rows;
                    expanded = [.. tree.ExpandedPaths];
                    showHidden = tree.ShowHidden;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FileBrowser] Listing failed under {_root}: {ex.Message}");
                    if (then is not null) _dispatcher.Post(then);
                    return;
                }

                _dispatcher.Post(() =>
                {
                    Publish(rows, expanded, showHidden);
                    then?.Invoke();
                });
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void Publish(IReadOnlyList<FileBrowserRow> rows, string[] expanded, bool showHidden)
    {
        if (_disposed) return;
        _treeRows = rows;
        _showHidden.Value = showHidden;
        _expanded = expanded;
        PublishRows();
        SyncPreview(PreviewSync.Follow);
        Persist();
    }

    /// <summary>Puts on the rail whichever list it should be showing. The one place that decides,
    /// so a listing landing mid-search cannot push the tree back over the results.</summary>
    private void PublishRows()
    {
        if (_disposed) return;
        _rows.Value = _finder.IsFiltering ? Found(_finder.Results.Value.Paths) : _treeRows;
    }

    /// <summary>
    /// The finder's paths as rows the rail can draw: flat, unindented, and each carrying the
    /// directory it came from as its dimmed second half — which is the whole reason a result row
    /// cannot be a tree row, since two files named the same are otherwise the same row twice.
    /// </summary>
    private IReadOnlyList<FileBrowserRow> Found(IReadOnlyList<string> relativePaths)
    {
        var rows = new List<FileBrowserRow>(relativePaths.Count);
        foreach (var relative in relativePaths)
        {
            var slash = relative.LastIndexOf('/');
            rows.Add(new FileBrowserRow.File(
                PathKey.Normalize(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar))),
                slash < 0 ? relative : relative[(slash + 1)..],
                Depth: 0,
                IsIgnored: false,
                IsHidden: false,
                IsLink: false,
                Guides: default)
            {
                Detail = slash < 0 ? null : relative[..slash],
            });
        }

        return rows;
    }

    private void Persist()
    {
        if (_disposed) return;
        _persist(new FileBrowserUiState
        {
            Expanded = _expanded.Select(ToRelative).OfType<string>().ToList(),
            ShowHidden = _showHidden.Value,
            // A declaration's key is not a path, and a restored one would point at a line the file
            // may no longer have. The file it lives in is not persisted in its place: the cursor was
            // on the declaration, and quietly moving it up a row on restart is its own small lie.
            Cursor = _cursor.Value is { } cursor && !cursor.Contains('\n') ? ToRelative(cursor) : null,
            RenderMarkdown = _renderMarkdown.Value,
            // Only what is inside the working tree: a tab on a file somewhere else is a place this
            // browser cannot name, and a restored one would point outside the repository it belongs
            // to. Transience is not kept — a session that starts by reopening a file is opening it.
            Tabs = _tabs.Items.Select(tab => ToRelative(tab.Path)).OfType<string>().ToList(),
            ActiveTab = _tabs.ActivePath is { } active ? ToRelative(active) : null,
        });
    }

    /// <summary>
    /// Points the preview at the active tab's file. A move to a different file shows <c>Loading</c>
    /// and then the file; a re-read of the file already on screen — every reconcile tick — stays
    /// silent and publishes only if the bytes actually moved.
    /// </summary>
    /// <remarks>
    /// The silence is the point. Republishing an identical preview rebuilds the body, and a body
    /// that rebuilds loses the reader's place in it; passing through <c>Loading</c> on the way
    /// empties the body first, which zeroes the scroll offset outright.
    /// </remarks>
    private void SyncPreview(PreviewSync sync)
    {
        if (_disposed) return;

        var target = _tabs.ActivePath;
        var samePath = string.Equals(target, _previewPath, StringComparison.Ordinal);
        if (sync == PreviewSync.Follow && samePath) return;

        if (!samePath)
        {
            _pendingReveal = null;
            _topVisibleLine = 0;
            UpdateBreadcrumb();
            // Folds belong to the file they were made in, and a fresh file starts open.
            _folds.Value = FoldState.Open(target ?? string.Empty);
        }
        _previewPath = target;
        _previewCancel?.Cancel();
        _previewCancel?.Dispose();
        _previewCancel = null;

        if (target is null)
        {
            _preview.Value = FilePreview.None.Instance;
            return;
        }

        var shown = _preview.Value;
        if (!samePath) _preview.Value = new FilePreview.Loading(target);

        var cancel = new CancellationTokenSource();
        _previewCancel = cancel;
        var generation = ++_previewGeneration;
        var token = cancel.Token;
        var dispatcher = _dispatcher;

        Task.Run(() =>
        {
            FilePreview result;
            try
            {
                result = FileContentLoader.Load(target, _extractor, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                result = new FilePreview.Unavailable(target, FilePreviewRefusal.Unreadable);
            }

            // Compared here rather than on the UI thread: both sides are immutable and the loading
            // thread already holds them, so an unchanged file costs the UI thread nothing at all.
            if (sync != PreviewSync.Rebuild && samePath && SaysTheSameThing(shown, result)) return;

            dispatcher.Post(() =>
            {
                if (_disposed || generation != _previewGeneration) return;
                _preview.Value = result;
                // The outline moved even when the viewport did not, so the same top line can mean a
                // different declaration than it did a moment ago.
                UpdateBreadcrumb();
                ReleasePendingReveal(result);
            });
        }, token);
    }

    /// <summary>
    /// Opens every collapsed declaration hiding <paramref name="line"/>, before anything tries to
    /// scroll to it. A jump into a folded body would otherwise land on the fold that swallowed it —
    /// the row stream has no row for a hidden line, so the scroll falls back to the nearest one
    /// above — and arriving somewhere other than where you asked is worse than not moving.
    /// </summary>
    /// <remarks>
    /// Runs before the reveal rather than after, because the two are one sequence: publishing the
    /// fold state re-flattens the rows synchronously, and only then does a line number mean a row.
    /// Ancestors are opened too — an outer fold hides an inner one's body whatever the inner one
    /// says — which is why this walks the whole containment chain instead of the innermost node.
    /// </remarks>
    private void Unfold(int line)
    {
        if (Outline is not { } outline) return;

        var folds = _folds.Value;
        if (folds.Collapsed.Count == 0) return;

        var opened = folds;
        string? parent = null;
        foreach (var node in outline.EnclosingPathAt(line))
        {
            var path = FileOutline.PathOf(parent, node);
            parent = path;
            // The declaration's own signature stays visible when it is folded, so a jump to it is
            // already a jump to something on screen.
            if (line >= Math.Max(node.StartLine + 1, node.SignatureEndLine) && opened.IsCollapsed(path))
                opened = opened.Toggled(path);
        }

        if (!ReferenceEquals(opened, folds)) _folds.Value = opened;
    }

    /// <summary>Hands over a line a caller asked for while the file was still being read. Only the
    /// file it was asked about, and only once it has text to scroll.</summary>
    private void ReleasePendingReveal(FilePreview published)
    {
        if (_pendingReveal is not { } pending) return;
        if (published is not FilePreview.Text text || text.Path != pending.Path) return;

        _pendingReveal = null;
        LineRevealRequested?.Invoke(pending.Line);
    }

    /// <summary>Whether a freshly loaded preview would draw exactly what is already on screen.
    /// Highlighting and the markdown render are derived from the same text, so the lines settle
    /// it; a picture is settled by the content hash the decoder already computed.</summary>
    private static bool SaysTheSameThing(FilePreview shown, FilePreview loaded) => (shown, loaded) switch
    {
        (FilePreview.Text a, FilePreview.Text b) =>
            a.Path == b.Path && a.WriteBack == b.WriteBack && a.Lines.SequenceEqual(b.Lines),
        (FilePreview.Image a, FilePreview.Image b) =>
            a.Path == b.Path && a.Preview.ContentHash == b.Preview.ContentHash,
        (FilePreview.Unavailable a, FilePreview.Unavailable b) => a == b,
        _ => false,
    };

    private string? Restored(string? relative)
    {
        if (relative is not { Length: > 0 }) return null;
        if (Path.IsPathRooted(relative)) return null;

        var absolute = PathKey.Normalize(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return ToRelative(absolute) is null ? null : absolute;
    }

    /// <summary>A restored path that is still a file on disk. A directory or a path that has gone
    /// is not something a tab can show.</summary>
    private string? RestoredFile(string? relative) =>
        Restored(relative) is { } absolute && File.Exists(absolute) ? absolute : null;

    private string? ToRelative(string absolute)
    {
        var relative = Path.GetRelativePath(_root, absolute).Replace('\\', '/');
        return relative is "." || relative.StartsWith("../", StringComparison.Ordinal) || relative == ".."
            ? null
            : relative;
    }

    public void Dispose()
    {
        _disposed = true;
        _previewCancel?.Cancel();
        _previewCancel?.Dispose();
        _searchRetarget.Dispose();
        _finder.Changed -= PublishRows;
        _finder.Dispose();
        _rows.Dispose();
        _cursor.Dispose();
        _showHidden.Dispose();
        _renderMarkdown.Dispose();
        _preview.Dispose();
        _breadcrumb.Dispose();
        _folds.Dispose();
        _tabs.Dispose();
    }
}
