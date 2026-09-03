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

public sealed record PreviewFile(DocumentUri Uri, LanguageId Language, PreviewContent Content);

public enum SkipReason
{
    NoServerForLanguage,
    PreviewTruncated,
}

/// <summary>What the pane is holding. Diagnostics live inside <see cref="Open"/> because there is
/// no such thing as diagnostics for a document that is not open — closing drops them with the
/// document rather than leaving them to be shown against the next file.</summary>
public abstract record DocumentState
{
    private DocumentState() { }

    public static readonly DocumentState Idle = new Nothing();

    public sealed record Nothing : DocumentState;

    public sealed record NotSent(SkipReason Reason) : DocumentState;

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

/// <summary>Hover as the protocol sends it: content, and optionally the range it describes.</summary>
public sealed record HoverReply(HoverPayload Content, OptionalRange Range);

/// <summary>The answer a hover request produced. <see cref="Discarded"/> is not a failure — it is
/// an answer that arrived after the selection moved, and applying it would put another file's
/// tooltip on this one.</summary>
public abstract record HoverAnswer
{
    private HoverAnswer() { }

    public static readonly HoverAnswer Discarded = new Stale();

    public static readonly HoverAnswer None = new Empty();

    public sealed record Stale : HoverAnswer;

    public sealed record Empty : HoverAnswer;

    public sealed record Content(HoverText Text, LspRange Range) : HoverAnswer;

    /// <summary>A hover with no range of its own anchors to the position that was asked about, so
    /// the popup always has somewhere to point.</summary>
    public static HoverAnswer For(HoverReply reply, LspPosition asked)
    {
        var markdown = HoverText.ToMarkdown(reply.Content);
        if (markdown.Length == 0) return None;

        var range = reply.Range is OptionalRange.Present present
            ? present.Range
            : LspRange.Empty(asked);
        return new Content(new HoverText(markdown), range);
    }
}

public abstract record DefinitionAnswer
{
    private DefinitionAnswer() { }

    public static readonly DefinitionAnswer Discarded = new Stale();

    public static readonly DefinitionAnswer NotFound = new Nowhere();

    public sealed record Stale : DefinitionAnswer;

    public sealed record Nowhere : DefinitionAnswer;

    public sealed record Targets(IReadOnlyList<DefinitionTarget> Items, OptionalRange Origin)
        : DefinitionAnswer;
}

/// <summary>
/// The answer a references request produced. The three ways of having no sites to show are
/// separate cases because they are three different sentences: a symbol nothing uses, a file that
/// left the screen before the answer arrived, and a server that would not answer the question.
/// Only the first is a zero.
/// </summary>
public abstract record ReferenceAnswer
{
    private ReferenceAnswer() { }

    public static readonly ReferenceAnswer Discarded = new Stale();

    public static readonly ReferenceAnswer NotFound = new Nowhere();

    public static readonly ReferenceAnswer NoAnswer = new Refused();

    public sealed record Stale : ReferenceAnswer;

    public sealed record Nowhere : ReferenceAnswer;

    /// <summary>The server was asked and did not answer — starting, failed, or unable at this
    /// position. Never a count.</summary>
    public sealed record Refused : ReferenceAnswer;

    public sealed record Sites(IReadOnlyList<DefinitionTarget> Items) : ReferenceAnswer;
}

/// <summary>
/// What the session needs from a running server, and nothing more. There is deliberately no way to
/// send an edit: the pane is read-only, the server reads the file from disk, and a method that
/// does not exist cannot be called by mistake.
/// </summary>
public interface ILanguageClient
{
    /// <summary>Whether any configured server claims this language. False is the whole cost of the
    /// feature when the user has no config file.</summary>
    bool Handles(LanguageId language);

    void OpenDocument(DocumentUri uri, LanguageId language, DocumentVersion version, string text);

    void CloseDocument(DocumentUri uri);

    Task<HoverReply> HoverAsync(DocumentUri uri, LspPosition position, CancellationToken cancel);

    Task<DefinitionPayload> DefinitionAsync(DocumentUri uri, LspPosition position, CancellationToken cancel);

    /// <summary>
    /// Every use of the symbol at a position, the declaration excluded — or null where the server
    /// did not answer at all.
    /// </summary>
    /// <remarks>
    /// An empty list and a refusal are held apart here, unlike everywhere else in this interface,
    /// because this answer is shown to a reader as a number. A server that is still starting, or
    /// has failed, refuses every question; counting those refusals as zero puts "no usages" over
    /// live code, which reads as a statement that it is dead.
    /// </remarks>
    Task<IReadOnlyList<Location>?> ReferencesAsync(
        DocumentUri uri, LspPosition position, CancellationToken cancel);

    /// <summary>Diagnostics are pushed, repeatedly, seconds apart, for as long as a document is
    /// open.</summary>
    event Action<PublishedDiagnostics>? DiagnosticsPublished;
}

/// <summary>
/// One open document at a time: the handle the Files pane holds for the file on screen. Previewing
/// a file opens it, previewing another closes it first, and a file the preview truncated is never
/// sent at all. Everything a server sends back is checked against the document that is open now,
/// so a late answer for the file that was on screen a moment ago is dropped rather than drawn.
/// </summary>
public sealed class PreviewSession : IDisposable
{
    private readonly ILanguageClient _client;
    private readonly RepoBoundary _boundary;

    private DocumentState _state = DocumentState.Idle;
    private string _openText = string.Empty;
    private DocumentVersion _nextVersion = new(1);
    private CancellationTokenSource? _requests;

    public PreviewSession(ILanguageClient client, RepoBoundary boundary)
    {
        _client = client;
        _boundary = boundary;
        _client.DiagnosticsPublished += OnDiagnosticsPublished;
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

        if (!_client.Handles(file.Language))
        {
            Publish(new DocumentState.NotSent(SkipReason.NoServerForLanguage));
            return;
        }

        if (file.Content is not PreviewContent.Complete complete)
        {
            Publish(new DocumentState.NotSent(SkipReason.PreviewTruncated));
            return;
        }

        var version = _nextVersion;
        _nextVersion = _nextVersion.Next();
        _openText = complete.Text;
        _requests = new CancellationTokenSource();
        _client.OpenDocument(file.Uri, file.Language, version, complete.Text);
        Publish(new DocumentState.Open(file.Uri, version, DiagnosticsState.Pending));
    }

    /// <summary>The selection moved to something that is not a file.</summary>
    public void Clear()
    {
        CloseOpenDocument();
        Publish(DocumentState.Idle);
    }

    public async Task<HoverAnswer> HoverAsync(LspPosition position)
    {
        if (Asking() is not (var uri, var version, var cancel)) return HoverAnswer.Discarded;

        HoverReply reply;
        try
        {
            reply = await _client.HoverAsync(uri, position, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return HoverAnswer.Discarded;
        }

        return StillShowing(uri, version) ? HoverAnswer.For(reply, position) : HoverAnswer.Discarded;
    }

    public async Task<DefinitionAnswer> DefinitionAsync(LspPosition position)
    {
        if (Asking() is not (var uri, var version, var cancel)) return DefinitionAnswer.Discarded;

        DefinitionPayload payload;
        try
        {
            payload = await _client.DefinitionAsync(uri, position, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return DefinitionAnswer.Discarded;
        }

        if (!StillShowing(uri, version)) return DefinitionAnswer.Discarded;

        var targets = DefinitionTargets.From(payload, _boundary);
        return targets.Count == 0
            ? DefinitionAnswer.NotFound
            : new DefinitionAnswer.Targets(targets, DefinitionTargets.OriginOf(payload));
    }

    public async Task<ReferenceAnswer> ReferencesAsync(LspPosition position)
    {
        if (Asking() is not (var uri, var version, var cancel)) return ReferenceAnswer.Discarded;

        IReadOnlyList<Location>? locations;
        try
        {
            locations = await _client.ReferencesAsync(uri, position, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return ReferenceAnswer.Discarded;
        }

        if (!StillShowing(uri, version)) return ReferenceAnswer.Discarded;
        if (locations is null) return ReferenceAnswer.NoAnswer;
        if (locations.Count == 0) return ReferenceAnswer.NotFound;

        return new ReferenceAnswer.Sites(
            locations.Select(site => _boundary.Classify(site.Uri, site.Range.Start)).ToArray());
    }

    public void Dispose()
    {
        _client.DiagnosticsPublished -= OnDiagnosticsPublished;
        CloseOpenDocument();
        _state = DocumentState.Idle;
        StateChanged = null;
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
        if (_state is DocumentState.Open open) _client.CloseDocument(open.Uri);
        _openText = string.Empty;
    }
}
