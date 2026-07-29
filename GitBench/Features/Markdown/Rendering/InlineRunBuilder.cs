using GitBench.Features.Markdown.Parsing;
using GitBench.Theming;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Maps the AST's flat <see cref="InlineRun"/>s onto <see cref="RichTextRun"/>s — the bridge
/// between Step 2's inline model and Step 4's rendering primitive. Strictly 1:1 and
/// order-preserving: the parser already merged adjacent same-style runs, so the builder never
/// merges or splits, and every output run keeps its input's exact text (hard-break "\n" runs
/// pass through as "\n" <see cref="RichTextRun"/>s — the layout interprets them).
/// <para>
/// The pinned style mapping (see <c>InlineRunBuilderTests</c>):
/// plain → default family, <paramref name="fontSize"/>, <paramref name="textColor"/>;
/// bold (or a true <paramref name="bold"/> base, e.g. headings) → <c>FontWeight.Bold</c>;
/// italic → <see cref="MarkdownFonts.ItalicFamily"/>; bold-italic → italic family + Bold;
/// code → <c>IsCode</c> + mono family (<c>DiffOptions.MonoFontFamily</c>) +
/// <see cref="MarkdownStyles.CodeChipText"/>; link → <c>LinkUrl</c> + <c>Underline</c> +
/// <see cref="MarkdownStyles.Link"/>. Strikethrough has no decoration channel in
/// <see cref="RichTextRun"/> yet and renders as plain text (deliberately unpinned).
/// </para>
/// </summary>
internal static class InlineRunBuilder
{
    /// <summary>Builds styled rich-text runs for one block's inline content.</summary>
    /// <param name="runs">The block's flat, pre-resolved inline runs.</param>
    /// <param name="styles">The active theme's markdown slots (link/chip colors).</param>
    /// <param name="fontSize">The block's font size (body text or a heading-ladder step).</param>
    /// <param name="textColor">The block's base text color for unstyled runs.</param>
    /// <param name="bold">True when the whole block is bold (headings): plain runs get Bold too.</param>
    public static IReadOnlyList<RichTextRun> Build(
        IReadOnlyList<InlineRun> runs,
        MarkdownStyles styles,
        float fontSize,
        uint textColor,
        bool bold = false)
    {
        throw new NotImplementedException("Step 5: InlineRunBuilder.Build is not implemented yet.");
    }
}
