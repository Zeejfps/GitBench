using GitBench.Theming;

namespace GitBench.Features.Diff;

/// <summary>
/// The highlighter the app uses: tree-sitter for every language we bundle a grammar and a
/// highlights query for, TextMate for the other fifty-odd, and TextMate again whenever tree-sitter
/// declines a file it nominally handles.
/// </summary>
/// <remarks>
/// <para>
/// The fallback is what makes this safe rather than a migration. Nothing a user sees today can get
/// worse: a language tree-sitter does not know never reaches it, and a file it refuses — over its
/// cap, over its budget, a grammar that failed to load — comes out of TextMate exactly as it does
/// now. The only way to render plain is for both engines to decline.
/// </para>
/// <para>
/// Measured over 1,014 files of this repository (2026-08-31), tree-sitter was 10.6x faster end to
/// end and not meaningfully slower on any file.
/// </para>
/// </remarks>
internal sealed class RoutedSyntaxHighlighter : ISyntaxHighlighter
{
    private readonly TreeSitterSyntaxHighlighter _treeSitter;
    private readonly ISyntaxHighlighter _textMate;

    public RoutedSyntaxHighlighter(TreeSitterSyntaxHighlighter treeSitter, ISyntaxHighlighter textMate)
    {
        _treeSitter = treeSitter;
        _textMate = textMate;
    }

    public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, FileLanguage language)
    {
        if (language is FileLanguage.TreeSitter(var parsed) && _treeSitter.Highlight(fileText, parsed) is { } spans)
        {
            return spans;
        }

        return _textMate.Highlight(fileText, language);
    }
}
