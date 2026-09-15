using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Lsp.Documents;

/// <summary>The version a pushed result claims. Servers are allowed to omit it, and most do, so
/// "untagged" is a case rather than a null.</summary>
public abstract record ResultVersion
{
    private ResultVersion() { }

    public static readonly ResultVersion Untagged = new None();

    public static ResultVersion At(DocumentVersion version) => new Tagged(version);

    public sealed record None : ResultVersion;

    public sealed record Tagged(DocumentVersion Version) : ResultVersion;
}

public sealed record PublishedDiagnostics(
    DocumentUri Uri,
    ResultVersion Version,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>What the preview holds for a file. The 2 MB cut-off drops the tail of the file and its
/// last partial line, so a truncated preview is not the file the server would read — it is a
/// different case, not a flag on the same one.</summary>
public abstract record PreviewContent
{
    private PreviewContent() { }

    public static PreviewContent Whole(string text) => new Complete(text);

    public static readonly PreviewContent Truncated = new CutShort();

    public sealed record Complete(string Text) : PreviewContent;

    public sealed record CutShort : PreviewContent;
}

public sealed record PreviewFile(DocumentUri Uri, PreviewContent Content);

/// <summary>What the pane is holding. Diagnostics live inside <see cref="Open"/> because there is
/// no such thing as diagnostics for a document that is not open — closing drops them with the
/// document rather than leaving them to be shown against the next file.</summary>
public abstract record DocumentState
{
    private DocumentState() { }

    public static readonly DocumentState Idle = new Nothing();

    public sealed record Nothing : DocumentState;

    /// <summary>The preview cut the file short, so the server was never told about it.</summary>
    public sealed record Truncated : DocumentState;

    public sealed record Open(DocumentUri Uri, DocumentVersion Version, DiagnosticsState Diagnostics)
        : DocumentState;
}

/// <summary>Whether the server has answered yet. An empty <see cref="Received"/> means the file is
/// clean, which is a different thing from not having heard back — the difference between "no
/// problems" and a spinner.</summary>
public abstract record DiagnosticsState
{
    private DiagnosticsState() { }

    public static readonly DiagnosticsState Pending = new Waiting();

    public sealed record Waiting : DiagnosticsState;

    public sealed record Received(IReadOnlyList<Diagnostic> Diagnostics) : DiagnosticsState;
}

/// <summary>
/// What a server said about a symbol: where it is declared, and the span of the symbol itself back
/// in the asking file when the server bothered to say. The span is what the link is drawn over —
/// without it the caller falls back to the word it found on screen, which is right for every server
/// and exact for the ones that answer about a qualified name rather than the word under the cursor.
/// </summary>
public sealed record DefinitionReply(IReadOnlyList<DefinitionTarget> Targets, OptionalRange Origin)
{
    public static readonly DefinitionReply Nothing = new([], OptionalRange.Absent);
}

/// <summary>
/// Where a symbol is used, the declaration itself excluded — so the number of sites is the number
/// a reader is shown.
/// </summary>
/// <remarks>
/// A symbol nothing uses and a question that could not be put are held apart, rather than both
/// arriving as an empty list, because the count is shown as a sentence about the code: "no usages"
/// over a symbol whose server never started says the code is dead, which is the one thing this
/// feature must never say by accident. <see cref="Answered"/> with an empty list is the real zero.
/// </remarks>
public abstract record ReferenceReply
{
    private ReferenceReply() { }

    /// <summary>Nobody could be asked: no server for the file, a server that does not answer the
    /// question, one that never finished starting, a file it could not be shown — or a file that
    /// left the screen before the answer arrived.</summary>
    public sealed record Unavailable : ReferenceReply
    {
        public static readonly Unavailable Instance = new();
    }

    public sealed record Answered(IReadOnlyList<DefinitionTarget> Sites) : ReferenceReply;
}

/// <summary>
/// One open document at a time: the handle the Files pane holds for the file on screen. Previewing
/// a file opens it, previewing another closes it first, and a file the preview truncated is never
/// sent at all. Everything a server sends back is checked against the document that is open now,
/// so a late answer for the file that was on screen a moment ago is dropped rather than drawn.
/// </summary>
public sealed class PreviewSession : IDisposable
{
    private readonly ILanguageServerQuestions _server;
    private readonly LanguageServerEntry _entry;
    private readonly RepoBoundary _boundary;
    private readonly AskAgainPolicy _retry;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly CancellationTokenSource _closing = new();

    private DocumentState _state = DocumentState.Idle;
    private string _openText = string.Empty;
    private DocumentVersion _nextVersion = new(1);
    private CancellationTokenSource? _requests;

    public PreviewSession(
        ILanguageServerQuestions server,
        LanguageServerEntry entry,
        RepoBoundary boundary,
        AskAgainPolicy retry,
        Func<TimeSpan, CancellationToken, Task> wait)
    {
        _server = server;
        _entry = entry;
        _boundary = boundary;
        _retry = retry;
        _wait = wait;
        _server.DiagnosticsPublished += OnDiagnosticsPublished;
    }

    public DocumentState State => _state;

    /// <summary>Raised whenever what the pane is holding changes: a new file, a file dropped, or a
    /// fresh wave of diagnostics for the file already open.</summary>
    public event Action<DocumentState>? StateChanged;

    /// <summary>Shows a file. Also the way a file that changed on disk is handled: same call, new
    /// content, so the watcher and the selection take one path rather than two.</summary>
    public void Preview(PreviewFile file)
    {
        if (_state is DocumentState.Open open
            && open.Uri == file.Uri
            && file.Content is PreviewContent.Complete same
            && same.Text == _openText)
            return;

        CloseOpenDocument();

        if (file.Content is not PreviewContent.Complete complete)
        {
            Publish(new DocumentState.Truncated());
            return;
        }

        var version = _nextVersion;
        _nextVersion = _nextVersion.Next();
        _openText = complete.Text;
        _requests = new CancellationTokenSource();
        _ = _server.OpenAsync(file.Uri, _entry.Language, version, complete.Text, _closing.Token);
        Publish(new DocumentState.Open(file.Uri, version, DiagnosticsState.Pending));
    }

    /// <summary>The selection moved to something that is not a file.</summary>
    public void Clear()
    {
        CloseOpenDocument();
        Publish(DocumentState.Idle);
    }

    /// <summary>What the server says about a position, as markdown — or null when it said nothing,
    /// or the answer arrived for a file that has since left the screen.</summary>
    public async Task<HoverText?> HoverAsync(LspPosition position)
    {
        if (Asking() is not (var uri, var version, var cancel)) return null;

        var response = await AskAsync(LspRequests.Hover(uri, position), cancel).ConfigureAwait(false);
        return StillShowing(uri, version) && response is LspResponse<Hover>.Ok(var hover)
            ? HoverText.Of(hover)
            : null;
    }

    public async Task<DefinitionReply> DefinitionAsync(LspPosition position)
    {
        if (Asking() is not (var uri, var version, var cancel)) return DefinitionReply.Nothing;

        var response = await AskAsync(LspRequests.Definition(uri, position), cancel).ConfigureAwait(false);
        if (!StillShowing(uri, version)) return DefinitionReply.Nothing;
        if (response is not LspResponse<Definition>.Ok(Definition.Targets targets)) return DefinitionReply.Nothing;

        // The selection range is the name; the enclosing range is the whole declaration with its
        // doc comment and attributes above it. Landing on the name is what the user asked for. The
        // origin is read off the first target only: several targets for one symbol are alternative
        // declarations of the same span, and a reader can only be pointing at one thing.
        return new DefinitionReply(
            targets.Items.Select(item => _boundary.Classify(item.Uri, item.Range.Start)).ToArray(),
            targets.Items[0].OriginRange);
    }

    /// <summary>
    /// Every use of the symbol at a position, the declaration excluded. Only an Ok is an answer: a
    /// server still starting, or one that has failed, refuses every question the same way, and a
    /// refusal counted as zero would be drawn as "no usages" over code that is used.
    /// </summary>
    public async Task<ReferenceReply> ReferencesAsync(LspPosition position)
    {
        if (Asking() is not (var uri, var version, var cancel)) return ReferenceReply.Unavailable.Instance;

        var response = await AskAsync(
                LspRequests.References(uri, position, includeDeclaration: false), cancel)
            .ConfigureAwait(false);
        if (!StillShowing(uri, version)) return ReferenceReply.Unavailable.Instance;

        return response switch
        {
            LspResponse<References>.Ok(References.Sites sites) => new ReferenceReply.Answered(
                sites.Items.Select(site => _boundary.Classify(site.Uri, site.Range.Start)).ToArray()),
            LspResponse<References>.Ok => new ReferenceReply.Answered([]),
            _ => ReferenceReply.Unavailable.Instance,
        };
    }

    public void Dispose()
    {
        if (_closing.IsCancellationRequested) return;
        _server.DiagnosticsPublished -= OnDiagnosticsPublished;
        CloseOpenDocument();
        _closing.Cancel();
        _closing.Dispose();
        _state = DocumentState.Idle;
        StateChanged = null;
    }

    /// <summary>
    /// Asks, asking again while the server says it is not ready. A request that outlives the
    /// source it was handed — closed underneath it on the way to the transport — reads as the
    /// cancellation it means rather than as a thrown object-disposed.
    /// </summary>
    private async Task<LspResponse<T>> AskAsync<T>(LspRequest<T> request, CancellationToken cancel)
    {
        try
        {
            return await AskAgain
                .AskAsync(
                    token => _server.AskAsync(request, _entry.RequestTimeout, token),
                    _retry,
                    _wait,
                    cancel)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return new LspResponse<T>.Cancelled();
        }
    }

    private bool StillShowing(DocumentUri uri, DocumentVersion version) =>
        _state is DocumentState.Open now && now.Uri == uri && now.Version == version;

    private void OnDiagnosticsPublished(PublishedDiagnostics published)
    {
        if (_state is not DocumentState.Open open) return;
        if (published.Uri != open.Uri) return;
        if (published.Version is ResultVersion.Tagged tagged && tagged.Version.Value < open.Version.Value) return;

        Publish(open with { Diagnostics = new DiagnosticsState.Received(published.Diagnostics.ToArray()) });
    }

    private void Publish(DocumentState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>
    /// What a request is about to be asked about: which file, at which version, and the token that
    /// ends the wait once it stops being the file on screen. Null when nothing is open.
    /// </summary>
    /// <remarks>
    /// The three are read as one because they are only meaningful together, and the source is read
    /// before the state deliberately: closing drops the source first, so a file leaving between the
    /// two reads is caught by the state check rather than by a null on the way to the token. A
    /// source disposed in the same gap reads as nothing open, which is what it means.
    /// </remarks>
    private (DocumentUri Uri, DocumentVersion Version, CancellationToken Cancel)? Asking()
    {
        var requests = _requests;
        if (requests is null || _state is not DocumentState.Open open) return null;

        try
        {
            return (open.Uri, open.Version, requests.Token);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private void CloseOpenDocument()
    {
        // Dropped before it is cancelled, so a request starting alongside this sees "nothing open"
        // rather than a source being disposed underneath it. Cancelling runs continuations, which
        // is not work to do while holding the field every other thread is reading.
        var requests = _requests;
        _requests = null;
        if (requests is not null)
        {
            requests.Cancel();
            requests.Dispose();
        }
        if (_state is DocumentState.Open open) _ = _server.CloseAsync(open.Uri, _closing.Token);
        _openText = string.Empty;
    }
}
