using GitBench.Lsp;
using GitBench.Lsp.Documents;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// What the servers currently say about the file the pane is showing. Carries the document's whole
/// state rather than a list, because a file nobody checked, a file still being checked, and a file
/// checked and found clean all have no diagnostics and are three different things to show.
/// </summary>
/// <remarks>
/// The path and the document are checked against each other rather than trusted to agree. A file
/// goes on screen before the server has been told about it, so there is a moment where the path is
/// the new file and the open document is still the previous one — and reporting that document's
/// errors under this path would underline one file's lines with another file's problems.
/// </remarks>
internal sealed record FileDiagnostics(string Path, DocumentState Document)
{
    public static readonly FileDiagnostics None = new(string.Empty, DocumentState.Idle);

    public IReadOnlyList<Diagnostic> Items =>
        Open is { Diagnostics: DiagnosticsState.Received received } ? received.Diagnostics : [];

    /// <summary>True once a server has answered for this file, whatever it said. An empty list with
    /// this false is a spinner; with it true, a clean file.</summary>
    public bool Answered => Open is { Diagnostics: DiagnosticsState.Received };

    public bool IsFor(string path) =>
        Path.Length > 0 && string.Equals(Path, path, StringComparison.Ordinal);

    private DocumentState.Open? Open =>
        Path.Length > 0
        && Document is DocumentState.Open open
        && open.Uri == DocumentUri.OfFile(Path)
            ? open
            : null;
}
