using GitBench.Features.Markdown.Parsing;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Renders one fenced <see cref="CodeBlock"/>: a themed box
/// (<c>MarkdownStyles.CodeBlockBackground</c>/<c>CodeBlockBorder</c>) holding the block's
/// verbatim text in the mono family (<c>DiffOptions.MonoFontFamily</c>), plus a copy button.
/// <para>
/// Pinned behavior (see <c>MarkdownWidgetTests</c>):
/// when <see cref="CodeBlock.IsClosed"/> is true and <see cref="CodeBlock.Language"/> resolves a
/// grammar, lines are colored via <c>SyntaxHighlighter.Highlight(Text, Language)</c> with slot
/// colors from the active theme's <c>DiffContent.Syntax</c> (the same slots the diff uses);
/// while the fence is open, or when the language is null/unknown, the text renders plain in
/// <c>MarkdownStyles.CodeBlockText</c> — verbatim either way, one visual line per source line,
/// never inline-parsed. Long lines live inside a <c>HorizontalScrollArea</c> (structure pinned,
/// not scroll physics). The copy button is labeled/tooltipped with the localized
/// <c>markdown.copy_code</c> string and writes <see cref="CodeBlock.Text"/> to the context's
/// <c>IClipboard</c> (no clipboard registered → button is inert, never a throw). Code text pins
/// <c>BaseDirection.Ltr</c> like the diff's mono runs.
/// </para>
/// </summary>
internal sealed record CodeBlockWidget : Widget
{
    /// <summary>The code block to render.</summary>
    public required CodeBlock Block { get; init; }

    protected override IWidget Build(Context ctx)
    {
        throw new NotImplementedException("Step 5: CodeBlockWidget.Build is not implemented yet.");
    }
}
