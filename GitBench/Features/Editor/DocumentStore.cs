using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Infrastructure;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Editor;

/// <summary>A file with edits that are not on disk, and the repository whose working tree it is
/// in.</summary>
internal readonly record struct UnsavedFile(Guid RepoId, string Path);

/// <summary>The one place edited files live: one set per repository, for the app session.</summary>
internal interface IDocumentStore
{
    /// <summary>One repository's edited files. The same set for the life of the repository.</summary>
    IRepoDocuments For(Guid repoId);

    /// <summary>Every file anywhere in the application with edits that are not on disk.</summary>
    IReadOnlyList<UnsavedFile> Unsaved();

    /// <summary>What a file being typed into holds right now, or null where it has nothing on top of
    /// the file on disk.</summary>
    string? UnsavedTextAt(string path);

    /// <summary>Records a file as saying what is now on disk, wherever it is open. Must be called on
    /// the UI thread in the same turn the write finished.</summary>
    void MarkSaved(string path);

    /// <summary>Raised on the UI thread with the absolute path of a file that was just typed into.</summary>
    event Action<string>? Edited;

    /// <summary>Raised on the UI thread as a file becomes editable, with the buffer that will stand
    /// for it until it is closed. The one place anything outside can learn that a document exists.</summary>
    event Action<EditorBuffer>? Opened;

    /// <summary>Raised on the UI thread as a buffer is forgotten — closed, or replaced because the
    /// file on disk changed under it. Nothing may hold it afterwards.</summary>
    event Action<EditorBuffer>? Closed;
}

/// <summary>One repository's files open for editing, keyed by absolute path.</summary>
internal interface IRepoDocuments
{
    /// <summary>The buffer for a file the preview has just read — the one already open for it, or a
    /// new one over this read. Null where the file is not editable at all.</summary>
    EditorBuffer? Open(
        string path, FileText text, FileWriteBack writeBack, DiffHighlight? highlight);

    /// <summary>Whether a file has edits the file on disk does not have.</summary>
    bool HasUnsavedEdits(string path);

    /// <summary>What a file being typed into holds right now, or null where it has nothing on top of
    /// the file on disk.</summary>
    string? UnsavedTextAt(string path);

    /// <summary>Every file here with unsaved edits, in the order they were opened.</summary>
    IReadOnlyList<string> Unsaved();

    /// <summary>Every file open here, each with the file on disk as the application last saw it.</summary>
    IReadOnlyList<OpenDocument> OpenDocuments();

    /// <summary>Settles what a fresh look at the file found, and takes it as the state the next look
    /// is compared against. A session re-stamped since <paramref name="expected"/> was read answers
    /// <see cref="DocumentReconciliation.Unchanged"/>.</summary>
    DocumentReconciliation Reconcile(string path, FileStamp? expected, FileStamp? found);

    /// <summary>Forgets a file's session, and with it whatever was typed into it.</summary>
    void Close(string path);

    /// <summary>Records the document as saying what is now on disk. Must be called on the UI thread
    /// in the same turn the write finished, or the write's own watcher echo asks the reader about
    /// their own keystrokes.</summary>
    void MarkSaved(string path);
}

/// <summary>Owns every repository's edited files, keyed by repo id, so what was typed survives a tab
/// switch, a mode switch and a remount of the pane. UI thread only.</summary>
internal sealed class DocumentStore : IDocumentStore, IHostedService, IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly ILocalizationService _loc;

    private readonly Dictionary<Guid, RepoDocuments> _repos = new();
    private readonly int _thread = Environment.CurrentManagedThreadId;

    private IDisposable? _reposSub;
    private bool _started;
    private bool _disposed;

    public DocumentStore(IRepoRegistry registry, ILocalizationService loc)
    {
        _registry = registry;
        _loc = loc;
    }

    public event Action<string>? Edited;

    public event Action<EditorBuffer>? Opened;

    public event Action<EditorBuffer>? Closed;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _reposSub = _registry.Repos.Subscribe(_ => DropClosedRepos());
    }

    public IRepoDocuments For(Guid repoId)
    {
        AssertThread();
        if (_repos.TryGetValue(repoId, out var existing)) return existing;
        var documents = new RepoDocuments(_loc);
        documents.Edited += RaiseEdited;
        documents.Opened += RaiseOpened;
        documents.Closed += RaiseClosed;
        _repos[repoId] = documents;
        return documents;
    }

    public IReadOnlyList<UnsavedFile> Unsaved()
    {
        AssertThread();
        var unsaved = new List<UnsavedFile>();
        foreach (var (repoId, documents) in _repos)
            foreach (var path in documents.Unsaved())
                unsaved.Add(new UnsavedFile(repoId, path));
        return unsaved;
    }

    public string? UnsavedTextAt(string path)
    {
        AssertThread();
        foreach (var documents in _repos.Values)
            if (documents.UnsavedTextAt(path) is { } text) return text;
        return null;
    }

    public void MarkSaved(string path)
    {
        AssertThread();
        foreach (var documents in _repos.Values) documents.MarkSaved(path);
    }

    private void RaiseEdited(string path) => Edited?.Invoke(path);

    private void RaiseOpened(EditorBuffer buffer) => Opened?.Invoke(buffer);

    private void RaiseClosed(EditorBuffer buffer) => Closed?.Invoke(buffer);

    private void AssertThread()
    {
        if (Environment.CurrentManagedThreadId == _thread) return;
        throw new InvalidOperationException(
            $"Edited files belong to the thread that opened them (thread {_thread}); this is thread " +
            $"{Environment.CurrentManagedThreadId}. An off-thread reader hops through the UI " +
            "dispatcher first.");
    }

    private void DropClosedRepos()
    {
        if (_disposed || _repos.Count == 0) return;

        var open = _registry.Repos.Select(r => r.Id).ToHashSet();
        foreach (var id in _repos.Keys.Where(id => !open.Contains(id)).ToArray())
        {
            var documents = _repos[id];
            _repos.Remove(id);
            documents.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _reposSub?.Dispose();
        foreach (var documents in _repos.Values) documents.Dispose();
        _repos.Clear();
    }
}

/// <summary>One repository's edited files.</summary>
internal sealed class RepoDocuments : IRepoDocuments, IDisposable
{
    private readonly ILocalizationService _loc;
    private readonly Dictionary<string, Entry> _open = new(PathKey.Comparer);
    private readonly List<string> _order = [];
    private readonly int _thread = Environment.CurrentManagedThreadId;

    private readonly State<int> _changes = new(0);

    public RepoDocuments(ILocalizationService loc) => _loc = loc;

    public event Action<string>? Edited;

    /// <summary>Raised as a buffer is created here.</summary>
    public event Action<EditorBuffer>? Opened;

    /// <summary>Raised as a buffer is forgotten here.</summary>
    public event Action<EditorBuffer>? Closed;

    public EditorBuffer? Open(
        string path, FileText text, FileWriteBack writeBack, DiffHighlight? highlight)
    {
        AssertThread();
        var key = PathKey.Normalize(path);
        if (_open.TryGetValue(key, out var entry))
        {
            if (ReferenceEquals(entry.Read, text) && entry.WriteBack == writeBack) return entry.Buffer;
            if (entry.HasUnsavedEdits) return entry.Buffer;

            if (entry.WriteBack == writeBack &&
                string.Equals(entry.Buffer.Session.Document.Text, text.Text, StringComparison.Ordinal))
            {
                entry.Reread(text);
                // The re-read found the file saying what the document already does, so everything
                // computed from it describes this revision and not the one the buffer was built at.
                entry.Buffer.Reread();
                return entry.Buffer;
            }

            Forget(key);
        }

        if (EditorBuffer.TryOpen(key, text, writeBack, highlight, _loc) is not { } buffer) return null;

        var entered = new Entry(buffer, text, writeBack, FileStamp.Of(key));
        _open[key] = entered;
        _order.Add(key);
        entered.Follow(() => OnDocumentChanged(key));
        _changes.Value++;
        Opened?.Invoke(buffer);
        return buffer;
    }

    public bool HasUnsavedEdits(string path)
    {
        AssertThread();
        // Registers the caller as following this set; not dead.
        _ = _changes.Value;
        return _open.TryGetValue(PathKey.Normalize(path), out var entry) && entry.HasUnsavedEdits;
    }

    public string? UnsavedTextAt(string path)
    {
        AssertThread();
        return _open.TryGetValue(PathKey.Normalize(path), out var entry) && entry.HasUnsavedEdits
            ? entry.Buffer.Session.Document.Text
            : null;
    }

    public IReadOnlyList<string> Unsaved()
    {
        AssertThread();
        // Registers the caller as following this set; not dead.
        _ = _changes.Value;
        var unsaved = new List<string>();
        foreach (var key in _order)
            if (_open.TryGetValue(key, out var entry) && entry.HasUnsavedEdits) unsaved.Add(key);
        return unsaved;
    }

    public IReadOnlyList<OpenDocument> OpenDocuments()
    {
        AssertThread();
        var open = new List<OpenDocument>(_order.Count);
        foreach (var key in _order)
            if (_open.TryGetValue(key, out var entry)) open.Add(new OpenDocument(key, entry.KnownOnDisk));
        return open;
    }

    public DocumentReconciliation Reconcile(string path, FileStamp? expected, FileStamp? found)
    {
        AssertThread();
        if (!_open.TryGetValue(PathKey.Normalize(path), out var entry)) return DocumentReconciliation.Unchanged;
        if (entry.KnownOnDisk != expected) return DocumentReconciliation.Unchanged;
        if (found == expected) return DocumentReconciliation.Unchanged;

        entry.MarkOnDisk(found);
        return entry.HasUnsavedEdits ? DocumentReconciliation.Diverged : DocumentReconciliation.Reloaded;
    }

    public void Close(string path)
    {
        AssertThread();
        if (!Forget(PathKey.Normalize(path))) return;
        _changes.Value++;
    }

    public void MarkSaved(string path)
    {
        AssertThread();
        var key = PathKey.Normalize(path);
        if (!_open.TryGetValue(key, out var entry)) return;
        entry.MarkSaved(FileStamp.Of(key));
        _changes.Value++;
    }

    private bool Forget(string key)
    {
        if (!_open.Remove(key, out var entry)) return false;
        entry.Unfollow();
        _order.Remove(key);
        Closed?.Invoke(entry.Buffer);
        return true;
    }

    private void OnDocumentChanged(string key)
    {
        _changes.Value++;
        Edited?.Invoke(key);
    }

    private void AssertThread()
    {
        if (Environment.CurrentManagedThreadId == _thread) return;
        throw new InvalidOperationException(
            $"Edited files belong to the thread that opened them (thread {_thread}); this is thread " +
            $"{Environment.CurrentManagedThreadId}. An off-thread reader hops through the UI " +
            "dispatcher first.");
    }

    public void Dispose()
    {
        foreach (var entry in _open.Values)
        {
            entry.Unfollow();
            Closed?.Invoke(entry.Buffer);
        }
        _open.Clear();
        _order.Clear();
        _changes.Dispose();
    }

    private sealed class Entry
    {
        private int _saved;
        private Action? _onChanged;

        public Entry(
            EditorBuffer buffer, FileText read, FileWriteBack writeBack, FileStamp? onDisk)
        {
            Buffer = buffer;
            Read = read;
            WriteBack = writeBack;
            KnownOnDisk = onDisk;
            _saved = buffer.Session.Document.Revision;
        }

        public EditorBuffer Buffer { get; }

        /// <summary>The lines this document was built from, held for reference identity rather than
        /// for their text.</summary>
        public FileText Read { get; private set; }

        public FileWriteBack WriteBack { get; }

        /// <summary>The file this was read from, as the application last saw it. Null where the file
        /// is not there.</summary>
        public FileStamp? KnownOnDisk { get; private set; }

        public bool HasUnsavedEdits => Buffer.Session.Document.Revision != _saved;

        public void Follow(Action onChanged)
        {
            _onChanged = onChanged;
            Buffer.Session.Document.Changed += onChanged;
        }

        public void Unfollow()
        {
            if (_onChanged is null) return;
            Buffer.Session.Document.Changed -= _onChanged;
            _onChanged = null;
        }

        public void Reread(FileText read) => Read = read;

        public void MarkOnDisk(FileStamp? onDisk) => KnownOnDisk = onDisk;

        public void MarkSaved(FileStamp? onDisk)
        {
            _saved = Buffer.Session.Document.Revision;
            KnownOnDisk = onDisk;
        }
    }
}
