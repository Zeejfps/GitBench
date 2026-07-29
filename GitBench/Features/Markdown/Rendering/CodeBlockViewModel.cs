using GitBench.Features.Diff;
using GitBench.Features.Markdown.Parsing;
using GitBench.Infrastructure;
using GitBench.Theming;
using ZGF.Observable;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>One code block's highlighting state: the per-line token spans, or null for plain.</summary>
internal sealed record CodeBlockState(IReadOnlyList<IReadOnlyList<TokenSpan>>? Spans);

/// <summary>
/// Syntax highlighting for one fenced code block. Tokenizing runs on a background lane — the
/// <c>DiffViewModel</c> precedent — so <see cref="Spans"/> starts null (the block renders plain)
/// and flips once, when the pass lands. A still-open fence never tokenizes: its text changes with
/// every streamed chunk, and the closing fence rebuilds the block anyway.
/// </summary>
internal sealed class CodeBlockViewModel : ViewModelBase<CodeBlockState>
{
    /// <param name="highlighter">Overrides the shared highlighter. Left null by the app so the
    /// shared instance is first touched on the worker rather than during the build.</param>
    public CodeBlockViewModel(
        CodeBlock block, IUiDispatcher dispatcher, ISyntaxHighlighter? highlighter = null)
        : base(dispatcher, new CodeBlockState(null))
    {
        Spans = Slice(s => s.Spans);
        BeginTokenize(block, highlighter);
    }

    /// <summary>Per-line token spans in tab-expanded column space; null while the block is plain.</summary>
    public IReadable<IReadOnlyList<IReadOnlyList<TokenSpan>>?> Spans { get; }

    private void BeginTokenize(CodeBlock block, ISyntaxHighlighter? highlighter)
    {
        if (!block.IsClosed || block.Language is not { } language) return;

        // The shared highlighter is reached inside the job, never before it: its first touch
        // builds the TextMate registry, which is exactly the cost this lane keeps off the UI thread.
        RunBackground<IReadOnlyList<IReadOnlyList<TokenSpan>>>(
            work: () => ((highlighter ?? SyntaxHighlighter.Shared).Highlight(block.Text, language), null),
            onResult: (spans, _) => Update(s => s with { Spans = spans }));
    }
}
