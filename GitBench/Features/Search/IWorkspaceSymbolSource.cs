using GitBench.Lsp;

namespace GitBench.Features.Search;

/// <summary>A question put to one language server, and the answer it will give: its symbols, or
/// null when it could not say.</summary>
internal sealed record ServerSymbolQuestion(LanguageId Language, Task<IReadOnlyList<SymbolRow>?> Answer);

/// <summary>Workspace symbol search over the language servers that are already running.</summary>
internal interface IWorkspaceSymbolSource
{
    /// <summary>
    /// Puts the query to every server that is running and ready for the active repository, each
    /// given <paramref name="limit"/> to answer. Never starts one, so searching has no side effects.
    /// Call on the UI thread.
    /// </summary>
    IReadOnlyList<ServerSymbolQuestion> AskWorkspaceSymbols(string query, TimeSpan limit, CancellationToken cancel);

    /// <summary>The language whose server answers for a file, or null when none is configured.</summary>
    LanguageId? LanguageOf(string absolutePath);
}
