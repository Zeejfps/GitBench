using GitBench.Lsp.Configuration;
using GitBench.Lsp.Documents;

namespace GitBench.Lsp.Lifecycle;

/// <summary>
/// What a caller may ask a server that is already running: the opening exchange, a document to read
/// from disk, and a question about a position in it.
/// </summary>
/// <remarks>
/// Separate from <see cref="ILanguageServerProcess"/>, which is all the supervisor needs, because
/// the supervisor must not be able to ask a server anything: it decides what runs, and something
/// else decides what is asked. Splitting them is also what lets the asking side be driven by a fake
/// with no subprocess behind it.
/// </remarks>
public interface ILanguageServerQuestions
{
    /// <summary>Runs the opening exchange. Null when it worked; otherwise why it did not, in words
    /// a reader can act on.</summary>
    Task<string?> HandshakeAsync(TimeSpan timeout, CancellationToken cancel);

    ServerCapabilities? Capabilities { get; }

    /// <summary>Tells the server about a file, at the text on disk.</summary>
    Task OpenAsync(
        DocumentUri uri, LanguageId language, DocumentVersion version, string text, CancellationToken cancel);

    Task<LspResponse<T>> AskAsync<T>(LspRequest<T> request, TimeSpan timeout, CancellationToken cancel);

    /// <summary>Tells the server the file is no longer on screen, so it stops publishing about
    /// it.</summary>
    Task CloseAsync(DocumentUri uri, CancellationToken cancel);

    /// <summary>Diagnostics the server pushed, in waves, for as long as a document is open.</summary>
    event Action<PublishedDiagnostics>? DiagnosticsPublished;
}

/// <summary>
/// One server, seen whole: the thing the supervisor runs and the thing a caller asks. Two
/// interfaces rather than one because almost nothing needs both, and this is the name for the
/// handful of places that do.
/// </summary>
public interface ILanguageServerSession : ILanguageServerProcess, ILanguageServerQuestions;
