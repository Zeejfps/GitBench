namespace GitBench.Features.Markdown.Parsing;

/// <summary>
/// Step 2's inline resolver: takes the raw inline text of one paragraph, heading, or table cell
/// and produces the flat, pre-resolved <see cref="InlineRun"/> list the renderer consumes.
/// Covers the scoped subset (docs/plans/markdown-renderer.md): emphasis (<c>*</c>/<c>**</c>/
/// <c>***</c>/<c>_</c>), inline code (backtick runs, code wins over emphasis), strikethrough,
/// links, bare-URL autolinks, backslash escapes, and hard breaks. Nesting resolves into style
/// flags on flat runs; adjacent runs with identical styling merge; unmatched delimiters degrade
/// to literal text. Never throws.
/// </summary>
internal static class InlineParser
{
    /// <summary>Resolves <paramref name="text"/> into flat styled runs. Never throws.</summary>
    internal static IReadOnlyList<InlineRun> Parse(string text)
    {
        throw new NotImplementedException();
    }
}
