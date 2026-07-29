using GitBench.Controls;
using GitBench.Features.Markdown.Parsing;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Renders a parsed <see cref="MarkdownDocument"/> as a <c>Column</c> of block widgets — the
/// reusable markdown surface the assistant transcript (and anything else markdown-shaped)
/// embeds. Static per document: streaming identity/diffing is Step 7's <c>MarkdownBlockList</c>,
/// which rebuilds this widget's rows, not this widget's concern.
/// <para>
/// Block mapping (pinned by <c>MarkdownWidgetTests</c>):
/// headings → bold <see cref="RichText"/> on the fixed FontSize ladder
/// (H1 = Title 22, H2 = Heading 16, H3 = Default 14, H4–H6 = Body 13), heading text in
/// <c>Palette.TextStrong</c>; paragraphs → <see cref="RichText"/> at Body 13 in
/// <c>Palette.TextBody</c>, inline styling via <see cref="InlineRunBuilder"/>; lists → marker
/// gutter ("•" bullets, "n." numbers honoring <c>Start</c>, display-only Lucide
/// square/check-square glyphs for task items) plus nested, indented children; blockquotes →
/// themed accent bar + inset children, nesting stacks bars/insets; thematic break → thin themed
/// rule; code blocks → <see cref="CodeBlockWidget"/>; tables → <see cref="MarkdownTable"/>
/// (auto-sized columns in a horizontal scroll fallback, pinned by
/// <c>MarkdownWidgetTableTests</c>). Anything unknown renders as a skipped block, never a throw.
/// </para>
/// <para>
/// Context: theme via <c>ctx.Theme()</c>, localization via <c>L.T</c> (copy button), optional
/// <c>IPlatformShell</c>/<c>IClipboard</c> exactly as <see cref="RichText"/> and
/// <see cref="CodeBlockWidget"/> resolve them.
/// </para>
/// </summary>
internal sealed record MarkdownWidget : Widget
{
    /// <summary>The parsed document to render.</summary>
    public required MarkdownDocument Document { get; init; }

    // The quote accent bar's width; the bar always reads as a vertical stripe, taller than wide.
    private const float QuoteBarWidth = 3f;

    protected override IWidget Build(Context ctx) => new Column
    {
        Gap = Spacing.Md,
        CrossAxis = CrossAxisAlignment.Stretch,
        Children = BlockWidgets(Document.Blocks, static s => s.Palette.TextBody),
    };

    /// <summary>Builds the widget list for a block sequence — the document's top level and, via
    /// recursion, quote bodies and list-item bodies. <paramref name="bodyColor"/> is the paragraph
    /// text color for this nesting scope: <c>TextBody</c> at the top level, <c>QuoteText</c>
    /// inside a quote.</summary>
    private static IWidget[] BlockWidgets(
        IReadOnlyList<MarkdownBlock> blocks, Func<ThemeStyles, uint> bodyColor)
    {
        var widgets = new List<IWidget>(blocks.Count);
        foreach (var block in blocks)
        {
            if (BlockWidget(block, bodyColor) is { } widget)
                widgets.Add(widget);
        }
        return widgets.ToArray();
    }

    private static IWidget? BlockWidget(MarkdownBlock block, Func<ThemeStyles, uint> bodyColor) =>
        block switch
        {
            HeadingBlock heading => Heading(heading),
            ParagraphBlock paragraph => InlineText(paragraph.Runs, FontSize.Body, bold: false, bodyColor),
            CodeBlock code => new CodeBlockWidget { Block = code },
            ListBlock list => List(list, bodyColor),
            QuoteBlock quote => Quote(quote),
            TableBlock table => new MarkdownTable { Block = table },
            ThematicBreakBlock => Rule(),
            // Anything unknown degrades to a skipped block rather than throwing.
            _ => null,
        };

    private static IWidget Heading(HeadingBlock heading) => InlineText(
        heading.Runs, HeadingFontSize(heading.Level), bold: true, static s => s.Palette.TextStrong);

    // The fixed heading ladder off the shared type scale: H1 = Title, H2 = Heading, H3 = Default,
    // H4–H6 = Body (bold alone distinguishes them from body text).
    private static float HeadingFontSize(int level) => level switch
    {
        1 => FontSize.Title,
        2 => FontSize.Heading,
        3 => FontSize.Default,
        _ => FontSize.Body,
    };

    /// <summary>One block's inline content as a <see cref="RichText"/>: runs are rebuilt from the
    /// live theme styles (so a theme flip restyles them), chip/hover colors come from the same
    /// slot set.</summary>
    private static IWidget InlineText(
        IReadOnlyList<InlineRun> runs, float fontSize, bool bold, Func<ThemeStyles, uint> textColor) =>
        new RichText
        {
            Runs = Prop.Deferred<IReadOnlyList<RichTextRun>>(ctx => ctx.Theme().Styles.Bind(
                s => InlineRunBuilder.Build(runs, s.Markdown, fontSize, textColor(s), bold))),
            CodeChipBackground = Theme.Color(s => s.Markdown.CodeChipBackground),
            LinkHoverColor = Theme.Color(s => s.Markdown.LinkHover),
        };

    private static IWidget List(ListBlock list, Func<ThemeStyles, uint> bodyColor) => new Column
    {
        Gap = Spacing.Xs,
        CrossAxis = CrossAxisAlignment.Stretch,
        Children = list.Items
            .Select(IWidget (item, index) => ListItemRow(list, item, index, bodyColor))
            .ToArray(),
    };

    // Marker gutter + the item's own blocks; a nested list arrives as one of those blocks and is
    // therefore indented past this item's marker automatically.
    private static IWidget ListItemRow(
        ListBlock list, ListItem item, int index, Func<ThemeStyles, uint> bodyColor) => new Row
    {
        Gap = Spacing.Sm,
        Children =
        [
            Marker(list, item, index, bodyColor),
            new Grow
            {
                Child = new Column
                {
                    Gap = Spacing.Xs,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children = BlockWidgets(item.Blocks, bodyColor),
                },
            },
        ],
    };

    private static IWidget Marker(
        ListBlock list, ListItem item, int index, Func<ThemeStyles, uint> bodyColor)
    {
        // GFM task item: a display-only Lucide checkbox glyph in place of the bullet/number.
        if (item.TaskChecked is { } isChecked)
        {
            return new Text
            {
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize.Body,
                Value = isChecked ? LucideIcons.CheckSquare : LucideIcons.Square,
                Color = Theme.Color(s => isChecked ? s.Palette.Accent : s.Palette.TextSecondary),
            };
        }

        return new Text
        {
            FontSize = FontSize.Body,
            Value = list.Ordered ? $"{list.Start + index}." : "•",
            Color = Theme.Color(bodyColor),
        };
    }

    private static IWidget Quote(QuoteBlock quote) => new Row
    {
        Gap = Spacing.Md,
        CrossAxis = CrossAxisAlignment.Stretch,
        Children =
        [
            new Box { Width = QuoteBarWidth, Background = Theme.Color(s => s.Markdown.QuoteBar) },
            new Grow
            {
                Child = new Column
                {
                    Gap = Spacing.Md,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    // Nested quotes recurse through here, stacking another bar + inset per level.
                    Children = BlockWidgets(quote.Blocks, static s => s.Markdown.QuoteText),
                },
            },
        ],
    };

    private static IWidget Rule() => new Box
    {
        Height = 1f,
        Background = Theme.Color(s => s.Markdown.Rule),
    };
}
