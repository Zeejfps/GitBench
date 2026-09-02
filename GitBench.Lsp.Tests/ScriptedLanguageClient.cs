namespace GitBench.Lsp.Documents.Tests;

/// <summary>
/// A language client the test drives by hand. Requests park until the test answers them, so
/// "the answer came back after the selection moved" is an ordering the test writes down rather
/// than a race it hopes for; diagnostics are pushed on demand, as a real server pushes them.
/// </summary>
public sealed class ScriptedLanguageClient : ILanguageClient
{
    public HashSet<string> Languages { get; } = ["rust", "go"];

    public List<OpenCall> Opened { get; } = [];

    public List<DocumentUri> Closed { get; } = [];

    public List<Pending<HoverReply>> Hovers { get; } = [];

    public List<Pending<DefinitionPayload>> Definitions { get; } = [];

    public event Action<PublishedDiagnostics>? DiagnosticsPublished;

    public bool Handles(LanguageId language) => Languages.Contains(language.Value);

    public void OpenDocument(DocumentUri uri, LanguageId language, DocumentVersion version, string text) =>
        Opened.Add(new OpenCall(uri, language, version, text));

    public void CloseDocument(DocumentUri uri) => Closed.Add(uri);

    public Task<HoverReply> HoverAsync(DocumentUri uri, LspPosition position, CancellationToken cancel)
    {
        var pending = new Pending<HoverReply>(uri, cancel);
        Hovers.Add(pending);
        return pending.Task;
    }

    public Task<DefinitionPayload> DefinitionAsync(DocumentUri uri, LspPosition position, CancellationToken cancel)
    {
        var pending = new Pending<DefinitionPayload>(uri, cancel);
        Definitions.Add(pending);
        return pending.Task;
    }

    public void Publish(DocumentUri uri, params Diagnostic[] diagnostics) =>
        Publish(uri, ResultVersion.Untagged, diagnostics);

    public void Publish(DocumentUri uri, ResultVersion version, params Diagnostic[] diagnostics) =>
        DiagnosticsPublished?.Invoke(new PublishedDiagnostics(uri, version, diagnostics));

    public sealed record OpenCall(DocumentUri Uri, LanguageId Language, DocumentVersion Version, string Text);

    public sealed class Pending<T>
    {
        // Continuations run inline on whichever thread answers, so a test that answers a request
        // and then awaits the session's task never has to wait on a scheduler.
        private readonly TaskCompletionSource<T> _completion = new();

        public Pending(DocumentUri uri, CancellationToken cancel)
        {
            Uri = uri;
            Cancel = cancel;
        }

        public DocumentUri Uri { get; }

        public CancellationToken Cancel { get; }

        public Task<T> Task => _completion.Task;

        public void Answer(T value) => _completion.TrySetResult(value);
    }
}
