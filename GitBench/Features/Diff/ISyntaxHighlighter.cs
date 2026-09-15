using GitBench.Theming;

namespace GitBench.Features.Diff;

/// <summary>
/// Tokenizing seam for every syntax-colored surface: file text plus its language in, one
/// <see cref="TokenSpan"/> list per source line out, null meaning "render plain". Implementations
/// are safe to call from a background thread and never throw to callers.
/// </summary>
internal interface ISyntaxHighlighter
{
    IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, FileLanguage language);
}
