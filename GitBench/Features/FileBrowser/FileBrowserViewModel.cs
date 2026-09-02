using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Markdown;
using GitBench.Git;
using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

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
    private readonly IIgnoreOracle _ignore;
    private readonly ISymbolExtractor _extractor;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<FileBrowserUiState> _persist;

    private readonly State<IReadOnlyList<FileBrowserRow>> _rows = new([]);
    private readonly State<string?> _cursor = new(null);
    private readonly State<bool> _showHidden = new(true);
    private readonly State<FilePreview> _preview = new(FilePreview.None.Instance);
    private readonly State<bool> _renderMarkdown = new(true);
    private readonly State<string?> _breadcrumb = new(null);
    private readonly State<FoldState> _folds = new(FoldState.Open(string.Empty));

    private readonly FileBrowserTabs _tabs = new();

    private readonly State<bool> _canGoBack = new(false);
    private readonly State<bool> _canGoForward = new(false);
    private readonly NavigationHistory<FileBrowserPlace> _history = new();

    private string[] _expanded = [];

    private FileBrowserTree? _tree;
    private Task _lane = Task.CompletedTask;
    private bool _disposed;

    private CancellationTokenSource? _previewCancel;
    private int _previewGeneration;
    private string? _previewPath;
    private (string Path, int Line)? _pendingReveal;
    private int _topVisibleLine;

    public FileBrowserViewModel(
        Repo repo,
        IFileSystemReader files,
        IIgnoreOracle ignore,
        ISymbolExtractor extractor,
        IUiDispatcher dispatcher,
        FileBrowserUiState restored,
        Action<FileBrowserUiState> persist)
    {
        _root = PathKey.Normalize(repo.Path);
        _files = files;
        _ignore = ignore;
        _extractor = extractor;
        _dispatcher = dispatcher;
        _persist = persist;

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
    private void Restore(FileBrowserUiState state)
    {
        foreach (var relative in state.Tabs)
            if (Restored(relative) is { } path) _tabs.Open(path, pinned: true);

        var cursor = Restored(state.Cursor);
        var active = Restored(state.ActiveTab) ?? cursor;
        if (active is not null) Show(active, pinned: true, line: null, record: false);
        if (cursor is not null) _cursor.Value = cursor;
    }

    /// <summary>The working tree this browser is rooted at.</summary>
    public string RootPath => _root;

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

    /// <summary>Which declarations are folded shut in the previewed file. Per file, UI thread only,
    /// and deliberately not persisted — a fold is a reading position, not a preference.</summary>
    public IReadable<FoldState> Folds => _folds;

    /// <summary>Folds or unfolds one declaration of the previewed file, by the id its row carries.</summary>
    public void ToggleFold(string id)
    {
        if (_disposed || _previewPath is not { } path) return;
        _folds.Value = (_folds.Value.Path == path ? _folds.Value : FoldState.Open(path)).Toggled(id);
    }

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

    public IReadable<bool> CanGoBack => _canGoBack;

    public IReadable<bool> CanGoForward => _canGoForward;

    /// <summary>Opens a file the reader asked for by name rather than by pointing at it — a
    /// definition jump. Pinned, because the trail of files a jump left behind is the thing the back
    /// button is for.</summary>
    public void NavigateTo(string absolutePath, int line)
    {
        if (_disposed) return;
        Travel(absolutePath, rowKey: null, line, pinned: true, record: true);
    }

    public void GoBack()
    {
        if (_disposed || !_history.TryGoBack(Here(), out var place)) return;
        Return(place);
    }

    public void GoForward()
    {
        if (_disposed || !_history.TryGoForward(Here(), out var place)) return;
        Return(place);
    }

    private void Return(FileBrowserPlace place)
    {
        UpdateHistory();
        Travel(place.AbsolutePath, place.RowKey, place.Line, pinned: false, record: false);
    }

    /// <summary>Shows a tab's file. Idempotent, so the strip can hand back the tab already on
    /// screen without it counting as a move.</summary>
    public void ActivateTab(FileBrowserTab tab)
    {
        if (_disposed || _tabs.Items.IndexOf(tab) < 0) return;
        Travel(tab.Path, rowKey: null, line: null, pinned: false, record: true);
    }

    public void CloseTab(FileBrowserTab tab)
    {
        if (_disposed) return;
        var wasActive = ReferenceEquals(_tabs.Active.Value, tab);
        _tabs.Close(tab);
        if (wasActive) FollowActiveTab();
        Persist();
    }

    public void CloseOtherTabs(FileBrowserTab keep)
    {
        if (_disposed) return;
        _tabs.CloseOthers(keep);
        FollowActiveTab();
        Persist();
    }

    public void CloseAllTabs()
    {
        if (_disposed) return;
        _tabs.CloseAll();
        FollowActiveTab();
        Persist();
    }

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

    /// <summary>Where the reader is, for the history to come back to. Null when nothing is open,
    /// which is not a place: there is nothing to return to.</summary>
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
    private void Travel(string absolutePath, string? rowKey, int? line, bool pinned, bool record)
    {
        var path = PathKey.Normalize(absolutePath);
        Show(path, pinned, line, record);

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
    /// Points the tabs — and through them the preview — at a file, remembering where the reader was
    /// before it. The one place a move is recorded, so nothing can move the preview without the
    /// back button knowing about it. Answers whether the strip now shows something else, which is
    /// what has to be written down.
    /// </summary>
    private bool Show(string path, bool pinned, int? line, bool record)
    {
        var leaving = Here();
        var previous = _tabs.Active.Value;
        var openCount = _tabs.Items.Count;
        var tab = _tabs.Open(path, pinned);
        var switched = !ReferenceEquals(previous, tab);

        if (record && leaving is not null && (switched || (line is { } asked && asked != leaving.Line)))
        {
            _history.Push(leaving);
            UpdateHistory();
        }

        SyncPreview(force: false);

        if (line is { } target) NavigateToLine(target);
        else if (switched && tab.TopLine > 0) NavigateToLine(tab.TopLine);

        return switched || _tabs.Items.Count != openCount;
    }

    /// <summary>Puts the cursor and the preview back on whatever the tabs are showing now — after a
    /// close, which is the one thing that moves the preview without the reader naming where to.</summary>
    private void FollowActiveTab()
    {
        SyncPreview(force: false);
        if (_tabs.Active.Value is not { } tab) return;
        if (HasRow(tab.Path)) _cursor.Value = tab.Path;
        if (tab.TopLine > 0) NavigateToLine(tab.TopLine);
    }

    private void UpdateHistory()
    {
        _canGoBack.Value = _history.CanGoBack;
        _canGoForward.Value = _history.CanGoForward;
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
        Persist();
    }

    public void SetShowHidden(bool show)
    {
        if (_showHidden.Value == show) return;
        _showHidden.Value = show;
        Queue(tree => tree.SetShowHidden(show));
    }

    /// <summary>Re-lists what is open and re-reads the previewed file. Bounded by what the reader
    /// opened, not by what is on disk — this runs twice a minute at idle on every platform, so it
    /// re-reads silently and publishes nothing when the bytes have not moved.</summary>
    public void Invalidate()
    {
        Queue(tree => tree.Refresh());
        SyncPreview(force: true);
    }

    /// <summary>Moves the cursor, and opens the file it landed on. Transiently: a cursor sweeping
    /// down a directory is looking, not opening, and the tab it borrows is handed to the next
    /// file rather than left behind.</summary>
    public void SetCursor(string rowKey)
    {
        if (_disposed) return;

        var opened = FileOf(rowKey) is { } file && Show(file, pinned: false, line: null, record: true);
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

        var opened = Show(row.FullPath, pinned: false, line: row.StartLine, record: true);
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
        switch (row)
        {
            case FileBrowserRow.Directory directory: Toggle(directory); break;
            case FileBrowserRow.Symbol symbol: SelectSymbol(symbol); Pin(symbol.FullPath); break;
            default: SetCursor(row.RowKey); Pin(row.FullPath); break;
        }
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
                        _files, _ignore, _root,
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
        _rows.Value = rows;
        _showHidden.Value = showHidden;
        _expanded = expanded;
        SyncPreview(force: false);
        Persist();
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
    private void SyncPreview(bool force)
    {
        if (_disposed) return;

        var target = _tabs.ActivePath;
        var samePath = string.Equals(target, _previewPath, StringComparison.Ordinal);
        if (!force && samePath) return;

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
            if (samePath && SaysTheSameThing(shown, result)) return;

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
            a.Path == b.Path && a.Truncated == b.Truncated && a.Lines.SequenceEqual(b.Lines),
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
        _rows.Dispose();
        _cursor.Dispose();
        _showHidden.Dispose();
        _renderMarkdown.Dispose();
        _preview.Dispose();
        _canGoBack.Dispose();
        _canGoForward.Dispose();
        _breadcrumb.Dispose();
        _folds.Dispose();
        _tabs.Dispose();
    }
}
