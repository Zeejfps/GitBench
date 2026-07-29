using GitBench.Theming;

namespace GitBench.Features.Diff;

/// <summary>
/// Tokenizing seam for every syntax-colored surface: file text plus a language id in, one
/// <see cref="TokenSpan"/> list per source line out, null meaning "render plain". Implementations
/// are safe to call from a background thread and never throw to callers.
/// </summary>
internal interface ISyntaxHighlighter
{
    /// <inheritdoc cref="SyntaxHighlighter.Highlight"/>
    IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, string languageId);
}
