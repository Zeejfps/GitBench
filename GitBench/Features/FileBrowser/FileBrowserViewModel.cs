using GitBench.Git;
using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// One repository's file browser: the rows on screen, the cursor, and the operations that move
/// either. Owns a <see cref="FileBrowserTree"/> and is the only thing that touches it.
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
/// </remarks>
internal sealed class FileBrowserViewModel : IDisposable
{
    private readonly string _root;
    private readonly IFileSystemReader _files;
    private readonly IIgnoreOracle _ignore;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<FileBrowserUiState> _persist;

    private readonly State<IReadOnlyList<FileBrowserRow>> _rows = new([]);
    private readonly State<string?> _cursor = new(null);
    private readonly State<bool> _showHidden = new(true);
    private readonly State<FilePreview> _preview = new(FilePreview.None.Instance);

    private string[] _expanded = [];

    private FileBrowserTree? _tree;
    private Task _lane = Task.CompletedTask;
    private bool _disposed;

    private CancellationTokenSource? _previewCancel;
    private int _previewGeneration;
    private string? _previewPath;

    public FileBrowserViewModel(
        Repo repo,
        IFileSystemReader files,
        IIgnoreOracle ignore,
        IUiDispatcher dispatcher,
        FileBrowserUiState restored,
        Action<FileBrowserUiState> persist)
    {
        _root = PathKey.Normalize(repo.Path);
        _files = files;
        _ignore = ignore;
        _dispatcher = dispatcher;
        _persist = persist;

        _showHidden.Value = restored.ShowHidden;
        _cursor.Value = Restored(restored.Cursor);

        var expanded = restored.Expanded.Select(Restored).OfType<string>().ToArray();
        var showHidden = restored.ShowHidden;
        Queue(tree =>
        {
            tree.SetShowHidden(showHidden);
            tree.RestoreExpanded(expanded);
        });
    }

    /// <summary>The working tree this browser is rooted at.</summary>
    public string RootPath => _root;

    public IReadable<IReadOnlyList<FileBrowserRow>> Rows => _rows;

    /// <summary>The row the keyboard is on, as an absolute path, or null when nothing is selected.</summary>
    public IReadable<string?> Cursor => _cursor;

    public IReadable<bool> ShowHidden => _showHidden;

    /// <summary>What the pane draws beside the tree: the cursor's file, or why it cannot be drawn.</summary>
    public IReadable<FilePreview> Preview => _preview;

    /// <summary>The tail of the serial lane. Held so a test can wait for the disk work it started
    /// rather than sleeping for it; nothing in the application awaits it.</summary>
    internal Task Pending => _lane;

    public void SetShowHidden(bool show)
    {
        if (_showHidden.Value == show) return;
        _showHidden.Value = show;
        Queue(tree => tree.SetShowHidden(show));
    }

    /// <summary>Re-lists what is open. Bounded by what the reader opened, not by what is on disk —
    /// this runs twice a minute at idle on every platform.</summary>
    public void Invalidate()
    {
        Queue(tree => tree.Refresh());
        SyncPreview(force: true);
    }

    public void SetCursor(string absolutePath)
    {
        if (_cursor.Value == absolutePath) return;
        _cursor.Value = absolutePath;
        SyncPreview(force: false);
        Persist();
    }

    public void Toggle(FileBrowserRow.Directory row)
    {
        SetCursor(row.FullPath);
        var path = row.FullPath;
        Queue(tree => tree.Toggle(path));
    }

    /// <summary>Enter, or a double-click: a directory opens or closes, a file is just selected — the
    /// preview follows the cursor, and handing a file to the OS is a menu item, never a gesture.</summary>
    public void Activate(FileBrowserRow row)
    {
        if (row is FileBrowserRow.Directory directory) Toggle(directory);
        else SetCursor(row.FullPath);
    }

    public void MoveCursor(int delta)
    {
        var rows = _rows.Value;
        if (rows.Count == 0) return;

        var current = IndexOfCursor(rows);
        var next = current < 0
            ? (delta > 0 ? 0 : rows.Count - 1)
            : Math.Clamp(current + delta, 0, rows.Count - 1);
        SetCursor(rows[next].FullPath);
    }

    /// <summary>Right arrow: open a closed directory, or step into an open one.</summary>
    public void ExpandOrDescend()
    {
        var rows = _rows.Value;
        var index = IndexOfCursor(rows);
        if (index < 0) return;

        if (rows[index] is not FileBrowserRow.Directory directory) return;
        if (!directory.IsExpanded) { Toggle(directory); return; }
        if (index + 1 < rows.Count && rows[index + 1].Depth > directory.Depth)
            SetCursor(rows[index + 1].FullPath);
    }

    /// <summary>Left arrow: close an open directory, or step out to the parent.</summary>
    public void CollapseOrAscend()
    {
        var rows = _rows.Value;
        var index = IndexOfCursor(rows);
        if (index < 0) return;

        if (rows[index] is FileBrowserRow.Directory { IsExpanded: true } open)
        {
            Toggle(open);
            return;
        }

        var depth = rows[index].Depth;
        for (var i = index - 1; i >= 0; i--)
        {
            if (rows[i].Depth >= depth) continue;
            SetCursor(rows[i].FullPath);
            return;
        }
    }

    public int IndexOfCursor(IReadOnlyList<FileBrowserRow> rows)
    {
        if (_cursor.Value is not { } cursor) return -1;
        for (var i = 0; i < rows.Count; i++)
            if (PathKey.Comparer.Equals(rows[i].FullPath, cursor)) return i;
        return -1;
    }

    private void Queue(Action<FileBrowserTree> work)
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
                    var tree = _tree ??= new FileBrowserTree(_files, _ignore, _root);
                    work(tree);
                    rows = tree.Rows;
                    expanded = [.. tree.ExpandedPaths];
                    showHidden = tree.ShowHidden;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FileBrowser] Listing failed under {_root}: {ex.Message}");
                    return;
                }

                _dispatcher.Post(() => Publish(rows, expanded, showHidden));
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
            Cursor = _cursor.Value is { } cursor ? ToRelative(cursor) : null,
        });
    }

    private void SyncPreview(bool force)
    {
        if (_disposed) return;

        var rows = _rows.Value;
        var index = IndexOfCursor(rows);
        var target = index >= 0 && rows[index] is FileBrowserRow.File file ? file.FullPath : null;
        if (!force && string.Equals(target, _previewPath, StringComparison.Ordinal)) return;

        _previewPath = target;
        _previewCancel?.Cancel();
        _previewCancel?.Dispose();
        _previewCancel = null;

        if (target is null)
        {
            _preview.Value = FilePreview.None.Instance;
            return;
        }

        _preview.Value = new FilePreview.Loading(target);

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
                result = FileContentLoader.Load(target, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                result = new FilePreview.Unavailable(target, FilePreviewRefusal.Unreadable);
            }

            dispatcher.Post(() =>
            {
                if (_disposed || generation != _previewGeneration) return;
                _preview.Value = result;
            });
        }, token);
    }

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
        _preview.Dispose();
    }
}
