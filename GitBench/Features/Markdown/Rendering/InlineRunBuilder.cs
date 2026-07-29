using GitBench.Features.Diff;
using GitBench.Features.Markdown.Parsing;
using GitBench.Theming;
using ZGF.Gui;

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
/// <see cref="MarkdownStyles.Link"/>; strikethrough → <c>Strikethrough</c>, which composes with
/// every other flag (a struck link keeps its underline and link color).
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
        if (runs.Count == 0)
            return Array.Empty<RichTextRun>();

        var result = new RichTextRun[runs.Count];
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var isLink = run.LinkUrl != null;

            // Each run gets its own TextStyle instance — the view hands styles to the canvas per
            // segment, so a shared mutated instance would alias (see RichTextRun's doc).
            var style = new TextStyle
            {
                FontSize = fontSize,
                TextColor = isLink ? styles.Link : run.Code ? styles.CodeChipText : textColor,
            };
            if (bold || run.Bold)
                style.FontWeight = FontWeight.Bold;
            if (run.Code)
                style.FontFamily = DiffOptions.MonoFontFamily;
            else if (run.Italic)
                style.FontFamily = MarkdownFonts.ItalicFamily;

            result[i] = new RichTextRun(
                run.Text,
                style,
                IsCode: run.Code,
                Underline: isLink,
                Strikethrough: run.Strikethrough,
                LinkUrl: run.LinkUrl);
        }

        return result;
    }
}
