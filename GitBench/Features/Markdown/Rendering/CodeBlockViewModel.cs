using GitBench.Features.Diff;
using GitBench.Features.Markdown.Parsing;
using GitBench.Git;
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
    public CodeBlockViewModel(CodeBlock block, IUiDispatcher dispatcher, ISyntaxHighlighter highlighter)
        : base(dispatcher, new CodeBlockState(null))
    {
        Spans = Slice(s => s.Spans);
        BeginTokenize(block, highlighter);
    }

    /// <summary>Per-line token spans in tab-expanded column space; null while the block is plain.</summary>
    public IReadable<IReadOnlyList<IReadOnlyList<TokenSpan>>?> Spans { get; }

    private void BeginTokenize(CodeBlock block, ISyntaxHighlighter highlighter)
    {
        if (!block.IsClosed || block.Language is not { } name) return;

        var language = FileLanguage.Named(name);
        RunBackground<Fetched<IReadOnlyList<IReadOnlyList<TokenSpan>>>>(
            work: () => new Fetched<IReadOnlyList<IReadOnlyList<TokenSpan>>>.Ok(
                highlighter.Highlight(block.Text, language)),
            onResult: spans =>
            {
                if (spans is Fetched<IReadOnlyList<IReadOnlyList<TokenSpan>>>.Ok ok)
                    Update(s => s with { Spans = ok.Value });
            });
    }
}
