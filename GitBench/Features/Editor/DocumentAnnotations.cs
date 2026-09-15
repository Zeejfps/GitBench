using System.Threading.Channels;

using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Editor;

/// <summary>
/// Keeps every open document's colouring and outline describing what is actually in it, by following
/// each edit into a parse tree held between them.
/// </summary>
/// <remarks>
/// <para>
/// It lives outside the document store rather than inside it, the same shape the language server's
/// text source takes: the store is UI-thread-only by construction and holds no disposable state, and
/// a native tree hung off one of its entries would leak per closed tab.
/// </para>
/// <para>
/// The UI thread posts two immutable values per edit — the revision the document reached, and the
/// edit that would undo it — and never touches a tree, a node or the parse bytes. The revision
/// travels with the message because only a document can mint one, and it is what
/// <see cref="EditorBuffer.Apply"/> checks the answer against: by the time a parse comes back the
/// reader may have typed again, and a parse of what they typed a moment ago is worse than none.
/// </para>
/// <para>
/// Only the languages tree-sitter colours are followed. TextMate covers the long tail and is an
/// order of magnitude slower, so a re-highlight there is a different proposition with a different
/// cost; those files keep today's behaviour, coloured at open and frozen after.
/// </para>
/// </remarks>
internal sealed class DocumentAnnotations : IHostedService, IDisposable
{
    /// <summary>How long a burst of typing is allowed to run before it is parsed. Far under the
    /// language server's, because an incremental re-parse of a keystroke is microseconds — what is
    /// being amortized here is the queries and the injection re-scan, not the parse.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);

    private readonly IDocumentStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly TreeSitterSyntaxHighlighter _highlighter;
    private readonly TreeSitterSymbolExtractor _extractor;
    private readonly TimeSpan _debounce;

    // UI thread only.
    private readonly Dictionary<int, Following> _following = new();
    private int _nextId;

    // Worker only.
    private readonly Dictionary<int, DocumentParse> _parses = new();
    private readonly Dictionary<int, DocumentRevision> _dirty = new();

    private readonly Channel<Message> _inbox =
        Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _settling = new();

    private TaskCompletionSource _settled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _outstanding;
    private Task? _worker;
    private bool _started;
    private bool _disposed;

    public DocumentAnnotations(
        IDocumentStore store,
        IUiDispatcher dispatcher,
        TreeSitterSyntaxHighlighter highlighter,
        TreeSitterSymbolExtractor extractor)
        : this(store, dispatcher, highlighter, extractor, Debounce)
    {
    }

    internal DocumentAnnotations(
        IDocumentStore store,
        IUiDispatcher dispatcher,
        TreeSitterSyntaxHighlighter highlighter,
        TreeSitterSymbolExtractor extractor,
        TimeSpan debounce)
    {
        _store = store;
        _dispatcher = dispatcher;
        _highlighter = highlighter;
        _extractor = extractor;
        _debounce = debounce;
        _settled.SetResult();
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        _store.Opened += Track;
        _store.Closed += Forget;
        _worker = Task.Run(() => Work(_stopping.Token));
    }

    /// <summary>Completes once every edit posted so far has been parsed and its annotations handed to
    /// the dispatcher. For the tests; nothing in the application waits on a parse.</summary>
    internal Task Settled()
    {
        lock (_settling) return _settled.Task;
    }

    private void Track(EditorBuffer buffer)
    {
        if (LanguageRegistry.DetectLanguageId(buffer.Path) is not { } languageId) return;
        // The tree-sitter engine's own answer, not the routed one's: a file it declines is coloured
        // by TextMate, and publishing an annotation with no highlight in it would blank that out.
        if (!_highlighter.Supports(languageId)) return;

        var id = ++_nextId;
        var outline = CodeLanguages.Detect(buffer.Path);
        var following = new Following(id, buffer, languageId, outline);
        _following[id] = following;

        following.OnEdited = edit => Post(new Message.Edited(id, edit.At, edit.Inverse, edit.Inserted));
        buffer.Edited += following.OnEdited;

        Post(Load(following));
    }

    private void Forget(EditorBuffer buffer)
    {
        foreach (var (id, following) in _following)
        {
            if (!ReferenceEquals(following.Buffer, buffer)) continue;

            buffer.Edited -= following.OnEdited;
            _following.Remove(id);
            Post(new Message.Dropped(id));
            return;
        }
    }

    // A mapping the worker could not follow. The document is the authority, so it is read again
    // here, on the thread that owns it, and the worker starts over from what it now says.
    private void Resync(int id)
    {
        if (!_following.TryGetValue(id, out var following)) return;
        Post(Load(following));
    }

    private static Message.Loaded Load(Following following) => new(
        following.Id,
        following.LanguageId,
        following.Outline,
        following.Buffer.Session.Document.Text,
        DocumentRevision.Of(following.Buffer.Session.Document));

    private void Publish(int id, DocumentRevision at, EditorAnnotations annotations)
    {
        if (!_following.TryGetValue(id, out var following)) return;
        following.Buffer.Apply(new Revised<EditorAnnotations>(at, annotations));
    }

    private void Post(Message message)
    {
        lock (_settling)
        {
            _outstanding++;
            if (_settled.Task.IsCompleted)
                _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (_inbox.Writer.TryWrite(message)) return;

        lock (_settling) Retire(1);
    }

    // Called on the worker once a batch has been handled and its annotations dispatched.
    private void Retire(int handled)
    {
        _outstanding -= handled;
        if (_outstanding <= 0) _settled.TrySetResult();
    }

    private async Task Work(CancellationToken cancellation)
    {
        try
        {
            while (await _inbox.Reader.WaitToReadAsync(cancellation).ConfigureAwait(false))
            {
                var handled = Drain();

                // The burst, not the keystroke: a run of typing arrives as many messages and is
                // worth one parse and one trip back to the UI thread.
                await Task.Delay(_debounce, cancellation).ConfigureAwait(false);
                handled += Drain();

                Emit();

                lock (_settling) Retire(handled);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var parse in _parses.Values) parse.Dispose();
            _parses.Clear();
        }
    }

    private int Drain()
    {
        var handled = 0;
        while (_inbox.Reader.TryRead(out var message))
        {
            Handle(message);
            handled++;
        }

        return handled;
    }

    private void Handle(Message message)
    {
        switch (message)
        {
            case Message.Loaded loaded:
            {
                if (_parses.Remove(loaded.Id, out var previous)) previous.Dispose();

                var parse = new DocumentParse(
                    _highlighter, _extractor, loaded.LanguageId, loaded.Outline, loaded.Text);
                if (!parse.Tracks)
                {
                    parse.Dispose();
                    _dirty.Remove(loaded.Id);
                    return;
                }

                _parses[loaded.Id] = parse;
                _dirty[loaded.Id] = loaded.At;
                return;
            }

            case Message.Edited edited:
            {
                if (!_parses.TryGetValue(edited.Id, out var parse)) return;

                if (!parse.Follow(edited.Inverse, edited.Inserted))
                {
                    _parses.Remove(edited.Id);
                    parse.Dispose();
                    _dirty.Remove(edited.Id);
                    var id = edited.Id;
                    _dispatcher.Post(() => Resync(id));
                    return;
                }

                // One undo is many edits and many revisions. The stamp is the last of them.
                _dirty[edited.Id] = edited.At;
                return;
            }

            case Message.Dropped dropped:
            {
                if (_parses.Remove(dropped.Id, out var parse)) parse.Dispose();
                _dirty.Remove(dropped.Id);
                return;
            }
        }
    }

    private void Emit()
    {
        if (_dirty.Count == 0) return;

        foreach (var (id, at) in _dirty)
        {
            if (!_parses.TryGetValue(id, out var parse)) continue;

            EditorAnnotations annotations;
            try
            {
                annotations = parse.Read();
            }
            catch (Exception)
            {
                // One file this cannot answer for stops being followed; it must not take the
                // worker, and with it every other open document, down with it.
                _parses.Remove(id);
                parse.Dispose();
                continue;
            }

            _dispatcher.Post(() => Publish(id, at, annotations));
        }

        _dirty.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_started)
        {
            _store.Opened -= Track;
            _store.Closed -= Forget;
        }

        foreach (var following in _following.Values) following.Buffer.Edited -= following.OnEdited;
        _following.Clear();

        _stopping.Cancel();
        _inbox.Writer.TryComplete();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stopping.Dispose();
    }

    private sealed class Following(int id, EditorBuffer buffer, string languageId, CodeLanguage? outline)
    {
        public int Id { get; } = id;

        public EditorBuffer Buffer { get; } = buffer;

        public string LanguageId { get; } = languageId;

        public CodeLanguage? Outline { get; } = outline;

        public Action<DocumentEdit>? OnEdited { get; set; }
    }

    private abstract record Message
    {
        /// <summary>A file to start following, or to start over from.</summary>
        public sealed record Loaded(
            int Id, string LanguageId, CodeLanguage? Outline, string Text, DocumentRevision At) : Message;

        public sealed record Edited(int Id, DocumentRevision At, TextEdit Inverse, string Inserted) : Message;

        public sealed record Dropped(int Id) : Message;
    }
}
