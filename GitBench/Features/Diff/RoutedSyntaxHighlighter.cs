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
/// Measured over 1,014 files of this repository, tree-sitter is 10.6x faster end to end and is not
/// meaningfully slower on any file; see <c>docs/plans/tree-sitter-highlighting-benchmark.md</c>.
/// That report predates injections and so has no Markdown or HTML row: both routed to TextMate
/// until the parser could follow a fenced block, a <c>&lt;script&gt;</c> body and Markdown's own
/// inline grammar into the languages they are written in.
/// </para>
/// </remarks>
internal sealed class RoutedSyntaxHighlighter : ISyntaxHighlighter, IDisposable
{
    /// <summary>
    /// The one instance every highlighting surface shares — diffs, the file browser and markdown
    /// code blocks alike.
    /// </summary>
    /// <remarks>
    /// Each instance builds a TextMate registry and compiles a query per bundled grammar, so a
    /// second one means paying both costs twice. First touch is what builds them: reach for this
    /// only from a background lane, never during a widget build.
    /// </remarks>
    public static RoutedSyntaxHighlighter Shared { get; } = new();

    private readonly TreeSitterSyntaxHighlighter _treeSitter;
    private readonly ISyntaxHighlighter _textMate;

    public RoutedSyntaxHighlighter(Action<string>? log = null)
        : this(new TreeSitterSyntaxHighlighter(log), SyntaxHighlighter.Shared)
    {
    }

    public RoutedSyntaxHighlighter(TreeSitterSyntaxHighlighter treeSitter, ISyntaxHighlighter textMate)
    {
        _treeSitter = treeSitter;
        _textMate = textMate;
    }

    /// <inheritdoc cref="SyntaxHighlighter.Highlight"/>
    public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, string languageId)
    {
        if (_treeSitter.Supports(languageId) && _treeSitter.Highlight(fileText, languageId) is { } spans)
        {
            return spans;
        }

        return _textMate.Highlight(fileText, languageId);
    }

    /// <summary>Whether a language would be colored by the parser rather than by regexes.</summary>
    public bool RoutesToTreeSitter(string languageId) => _treeSitter.Supports(languageId);

    public void Dispose() => _treeSitter.Dispose();
}
