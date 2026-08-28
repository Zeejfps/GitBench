using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.Markdown.Parsing;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Fonts;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Renders one fenced <see cref="CodeBlock"/>: a themed box
/// (<c>MarkdownStyles.CodeBlockBackground</c>/<c>CodeBlockBorder</c>) holding the block's
/// verbatim text in the mono family (<c>MonoFonts.Regular</c>), plus a copy button.
/// <para>
/// Pinned behavior (see <c>MarkdownWidgetTests</c>):
/// when <see cref="CodeBlock.IsClosed"/> is true and <see cref="CodeBlock.Language"/> resolves a
/// grammar, lines are colored with slot colors from the active theme's <c>DiffContent.Syntax</c>
/// (the same slots the diff uses) as soon as <see cref="CodeBlockViewModel"/>'s background pass
/// lands; until then, while the fence is open, or when the language is null/unknown, the text
/// renders plain in <c>MarkdownStyles.CodeBlockText</c> — verbatim either way, one visual line per
/// source line, never inline-parsed. Long lines live inside a <c>HorizontalScrollArea</c>
/// (structure pinned, not scroll physics). The copy button is labeled/tooltipped with the localized
/// <c>markdown.copy_code</c> string and writes <see cref="CodeBlock.Text"/> to the context's
/// <c>IClipboard</c> (no clipboard registered → button is inert, never a throw). Code text pins
/// <c>BaseDirection.Ltr</c> like the diff's mono runs.
/// </para>
/// </summary>
internal sealed record CodeBlockWidget : Widget<CodeBlockViewModel>
{
    /// <summary>The code block to render.</summary>
    public required CodeBlock Block { get; init; }

    // The highlighter is an optional context override so tests can count tokenize passes; the app
    // registers none, which leaves the shared instance to be resolved on the worker.
    protected override CodeBlockViewModel CreateState(Context ctx) =>
        new(Block, ctx.Require<IUiDispatcher>(), ctx.Get<ISyntaxHighlighter>());

    protected override IWidget Build(Context ctx, CodeBlockViewModel vm)
    {
        var block = Block;
        var theme = ctx.Theme().Styles;

        // Tab-expanded display lines, because token spans arrive in tab-expanded column space
        // (the highlighter expands the same way — see DiffText). Copy still uses the verbatim
        // Block.Text.
        var lines = SplitLines(block.Text);

        return new Box
        {
            Background = Theme.Color(s => s.Markdown.CodeBlockBackground),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Markdown.CodeBlockBorder)),
            BorderRadius = BorderRadiusStyle.All(Radius.Md),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Md),
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Sm,
                            Children =
                            [
                                new Grow
                                {
                                    Child = new HorizontalScrollArea
                                    {
                                        Child = new RichText
                                        {
                                            // Tracks both sources: the spans land once, a theme
                                            // flip re-resolves their colors without re-tokenizing.
                                            Runs = Prop.Bind(
                                                () => CodeRuns(lines, vm.Spans.Value, theme.Value)),
                                            SelectionBackground =
                                                Theme.Color(s => s.Markdown.SelectionBackground),
                                        },
                                    },
                                },
                                new CopyIconButton
                                {
                                    Label = static s => s.MarkdownCopyCode,
                                    GetText = () => block.Text,
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>One mono run per colored slice, '\n' runs between source lines — so the layout
    /// yields exactly one visual line per source line (empty lines included). Highlighted lines
    /// interleave slot-colored token runs with <c>CodeBlockText</c> gaps; plain lines are a single
    /// run, and a wholly unhighlighted block is one run of the joined text ('\n' breaks lines
    /// either way, so the layout is identical).</summary>
    private static IReadOnlyList<RichTextRun> CodeRuns(
        IReadOnlyList<string> lines,
        IReadOnlyList<IReadOnlyList<TokenSpan>>? spans,
        ThemeStyles theme)
    {
        // Style instances are cached per color: RichTextRun requires a stable instance per look,
        // and lines of the same color can share one safely (nothing mutates them after build).
        var styles = new Dictionary<uint, TextStyle>();

        RichTextRun Run(string text, uint color)
        {
            if (!styles.TryGetValue(color, out var style))
            {
                // Pinned LTR like the diff's mono grid: code is a left-origin monospace surface
                // and must not reorder or right-align under an RTL locale.
                style = new TextStyle
                {
                    FontFamily = MonoFonts.Regular,
                    FontSize = FontSize.Body,
                    TextColor = color,
                    BaseDirection = BidiDirection.Ltr,
                };
                styles[color] = style;
            }
            return new RichTextRun(text, style);
        }

        var plain = theme.Markdown.CodeBlockText;
        if (spans == null)
            return new[] { Run(string.Join("\n", lines), plain) };

        var runs = new List<RichTextRun>(lines.Count * 2);
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                runs.Add(Run("\n", plain));

            var line = lines[i];
            if (line.Length == 0)
                continue;

            var lineSpans = i < spans.Count ? spans[i] : null;
            if (lineSpans == null || lineSpans.Count == 0)
            {
                runs.Add(Run(line, plain));
                continue;
            }

            var cursor = 0;
            foreach (var span in lineSpans)
            {
                var start = Math.Clamp(span.Start, cursor, line.Length);
                var end = Math.Clamp(span.Start + span.Length, start, line.Length);
                if (start > cursor)
                    runs.Add(Run(line[cursor..start], plain));
                if (end > start)
                    runs.Add(Run(line[start..end], SlotColor(span.Slot, theme.DiffContent.Syntax, plain)));
                cursor = end;
            }
            if (cursor < line.Length)
                runs.Add(Run(line[cursor..], plain));
        }

        return runs;
    }

    // Slot → theme color, mirroring DiffRowPainter.SlotColor so markdown code and diff code
    // always agree; Default falls back to the block's plain text color.
    private static uint SlotColor(TokenColorSlot slot, DiffSyntaxStyles syntax, uint fallback) => slot switch
    {
        TokenColorSlot.Keyword => syntax.Keyword,
        TokenColorSlot.String => syntax.String,
        TokenColorSlot.Comment => syntax.Comment,
        TokenColorSlot.Number => syntax.Number,
        TokenColorSlot.Type => syntax.Type,
        TokenColorSlot.Function => syntax.Function,
        TokenColorSlot.Variable => syntax.Variable,
        TokenColorSlot.Operator => syntax.Operator,
        TokenColorSlot.Punctuation => syntax.Punctuation,
        TokenColorSlot.Constant => syntax.Constant,
        TokenColorSlot.Heading => syntax.Heading,
        TokenColorSlot.Emphasis => syntax.Emphasis,
        TokenColorSlot.Link => syntax.Link,
        TokenColorSlot.Code => syntax.Code,
        TokenColorSlot.Quote => syntax.Quote,
        _ => fallback,
    };

    // Splits like the highlighter does ('\n', tolerating '\r\n', always a final element), then
    // tab-expands each line so display columns line up 1:1 with the spans' column space.
    private static IReadOnlyList<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            var end = i;
            if (end > start && text[end - 1] == '\r')
                end--;
            lines.Add(DiffText.ExpandTabs(text[start..end]));
            start = i + 1;
        }
        lines.Add(DiffText.ExpandTabs(text[start..]));
        return lines;
    }
}
