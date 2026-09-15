using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Lsp.Documents.Tests;

/// <summary>
/// A language server the test drives by hand. Requests park until the test answers them, so
/// "the answer came back after the selection moved" is an ordering the test writes down rather
/// than a race it hopes for; diagnostics are pushed on demand, as a real server pushes them.
/// </summary>
public sealed class ScriptedLanguageServer : ILanguageServerQuestions
{
    private readonly List<object> _asked = [];

    public List<OpenCall> Opened { get; } = [];

    public List<DocumentUri> Closed { get; } = [];

    public IReadOnlyList<Pending<Hover>> Hovers => Asked<Hover>();

    public IReadOnlyList<Pending<Definition>> Definitions => Asked<Definition>();

    public IReadOnlyList<Pending<References>> References => Asked<References>();

    public event Action<PublishedDiagnostics>? DiagnosticsPublished;

    public ServerCapabilities? Capabilities => null;

    public Task<string?> HandshakeAsync(TimeSpan timeout, CancellationToken cancel) =>
        Task.FromResult<string?>(null);

    public Task OpenAsync(
        DocumentUri uri, LanguageId language, DocumentVersion version, string text, CancellationToken cancel)
    {
        Opened.Add(new OpenCall(uri, language, version, text));
        return Task.CompletedTask;
    }

    public Task CloseAsync(DocumentUri uri, CancellationToken cancel)
    {
        Closed.Add(uri);
        return Task.CompletedTask;
    }

    public Task<LspResponse<T>> AskAsync<T>(LspRequest<T> request, TimeSpan timeout, CancellationToken cancel)
    {
        var pending = new Pending<T>(cancel);
        _asked.Add(pending);
        return pending.Task;
    }

    public void Forget<T>() => _asked.RemoveAll(asked => asked is Pending<T>);

    public void Publish(DocumentUri uri, params Diagnostic[] diagnostics) =>
        Publish(uri, ResultVersion.Untagged, diagnostics);

    public void Publish(DocumentUri uri, ResultVersion version, params Diagnostic[] diagnostics) =>
        DiagnosticsPublished?.Invoke(new PublishedDiagnostics(uri, version, diagnostics));

    private IReadOnlyList<Pending<T>> Asked<T>() => _asked.OfType<Pending<T>>().ToList();

    public sealed record OpenCall(DocumentUri Uri, LanguageId Language, DocumentVersion Version, string Text);

    public sealed class Pending<T>
    {
        // Continuations run inline on whichever thread answers, so a test that answers a request
        // and then awaits the session's task never has to wait on a scheduler.
        private readonly TaskCompletionSource<LspResponse<T>> _completion = new();

        public Pending(CancellationToken cancel) => Cancel = cancel;

        public CancellationToken Cancel { get; }

        public Task<LspResponse<T>> Task => _completion.Task;

        public void Answer(T value) => _completion.TrySetResult(new LspResponse<T>.Ok(value));

        public void Refuse() =>
            _completion.TrySetResult(new LspResponse<T>.Failed(LspErrorCode.RequestFailed, "no"));

        /// <summary>Ends the request the way a transport does when the token it was handed has
        /// been disposed underneath it, rather than with an answer.</summary>
        public void Fail(Exception error) => _completion.TrySetException(error);
    }
}
