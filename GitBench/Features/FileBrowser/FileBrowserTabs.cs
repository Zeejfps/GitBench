using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// One open file in the browser: the path it shows, the name its tab wears, and the line the reader
/// was last on in it.
/// </summary>
/// <remarks>
/// A tab is opened transiently by looking at a file and pinned by asking for it — a click in the
/// tree replaces the transient tab where a double-click, a definition jump, or Enter leaves one
/// behind. Without that, arrowing down a directory would leave a tab per row and the strip would be
/// unusable within a second of touching the keyboard.
/// </remarks>
internal sealed class FileBrowserTab : IDisposable
{
    private readonly State<bool> _transient;

    public FileBrowserTab(string path, bool transient, long? openedAt = null)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        OpenedAt = openedAt ?? OpenOrder.Next();
        _transient = new State<bool>(transient);
    }

    /// <summary>Where this tab falls in the run the content panel shows — every kind of tab in it,
    /// not only the files. A tab that took a transient one's place inherits its place in the order
    /// along with its slot, and dragging the run into a new arrangement rewrites it.</summary>
    public long OpenedAt { get; internal set; }

    /// <summary>The file, absolute and normalized. A tab's identity: one tab per file.</summary>
    public string Path { get; }

    public string Name { get; }

    /// <summary>Whether the next transiently opened file takes this tab's place.</summary>
    public IReadable<bool> Transient => _transient;

    /// <summary>The top of the viewport when the reader last left this tab, restored on the way
    /// back in. Zero until they have been here.</summary>
    public int TopLine { get; set; }

    public void Pin() => _transient.Value = false;

    public void Dispose() => _transient.Dispose();
}

/// <summary>
/// The open tabs of one file browser, in the order they were opened, and which of them is on screen.
/// </summary>
/// <remarks>
/// The collection only: what opening a tab does to the preview, the tree cursor and the navigation
/// history is <see cref="FileBrowserViewModel"/>'s, which owns this and is the only thing that
/// mutates it.
/// </remarks>
internal sealed class FileBrowserTabs : IDisposable
{
    private readonly ObservableList<FileBrowserTab> _tabs = new();
    private readonly State<FileBrowserTab?> _active = new(null);
    private readonly Func<string, bool> _hasUnsavedEdits;

    public FileBrowserTabs(Func<string, bool> hasUnsavedEdits) => _hasUnsavedEdits = hasUnsavedEdits;

    public ObservableList<FileBrowserTab> Items => _tabs;

    public IReadable<FileBrowserTab?> Active => _active;

    /// <summary>The file the preview is pointed at, or null when nothing is open.</summary>
    public string? ActivePath => _active.Value?.Path;

    /// <summary>
    /// Opens <paramref name="path"/>, or activates the tab that already has it. A transient open
    /// takes the place of the tab the last one left — same slot, so the strip does not shuffle
    /// under the reader as they arrow down a directory.
    /// </summary>
    public FileBrowserTab Open(string path, bool pinned)
    {
        if (Find(path) is { } existing)
        {
            if (pinned) existing.Pin();
            _active.Value = existing;
            return existing;
        }

        var replacing = pinned ? -1 : IndexOfTransient();
        if (replacing < 0)
        {
            var tab = new FileBrowserTab(path, transient: !pinned);
            _tabs.Add(tab);
            _active.Value = tab;
            return tab;
        }

        var old = _tabs[replacing];
        // Same slot and the same place in the run: a transient open is the reader still looking at
        // one thing, and a tab that jumped to the end of the strip on every arrow key would be the
        // shuffling this replacement exists to avoid.
        var taking = new FileBrowserTab(path, transient: !pinned, openedAt: old.OpenedAt);
        _tabs.RemoveAt(replacing);
        _tabs.Insert(replacing, taking);
        old.Dispose();

        _active.Value = taking;
        return taking;
    }

    /// <summary>Closes one tab, handing the surface to its neighbour — the one that took its place
    /// in the strip, or the last tab when it was the last.</summary>
    public void Close(FileBrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;

        _tabs.RemoveAt(index);
        if (ReferenceEquals(_active.Value, tab))
            _active.Value = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        tab.Dispose();
    }

    public void CloseOthers(FileBrowserTab keep)
    {
        foreach (var tab in _tabs.ToArray())
            if (!ReferenceEquals(tab, keep)) Close(tab);
    }

    public void CloseAll()
    {
        foreach (var tab in _tabs.ToArray()) Close(tab);
    }

    public FileBrowserTab? Find(string path)
    {
        foreach (var tab in _tabs)
            if (PathKey.Comparer.Equals(tab.Path, path)) return tab;
        return null;
    }

    /// <summary>The tab a transient open takes the place of, or -1 when there is none to take. A
    /// tab holding unsaved edits is pinned here instead of being taken.</summary>
    private int IndexOfTransient()
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            if (!tab.Transient.Value) continue;
            if (_hasUnsavedEdits(tab.Path)) { tab.Pin(); continue; }
            return i;
        }
        return -1;
    }

    public void Dispose()
    {
        foreach (var tab in _tabs) tab.Dispose();
        _tabs.Clear();
        _active.Dispose();
    }
}
