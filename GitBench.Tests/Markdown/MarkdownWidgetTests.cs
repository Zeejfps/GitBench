using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests.Markdown;

// Harness tests for MarkdownWidget: parse real markdown with BasicMarkdownParser, mount the
// widget with the real ThemeService/LocalizationService, and pin the rendered draws — texts at
// the expected sizes/weights/colors, marker glyphs, quote bars, the themed rule, and the code
// block box with syntax coloring and a working copy button. Synthetic text metrics make geometry
// deterministic (8px per char, 16px line height); the y axis points up, so "below on screen"
// means a smaller Bottom.
//
// Pinned contracts (binding on the implementer):
// - Headings: fixed FontSize ladder H1=22/H2=16/H3=14/H4-H6=13, FontWeight.Bold, Palette.TextStrong.
// - Paragraph body: FontSize.Body (13) in Palette.TextBody; inline styling via InlineRunBuilder.
// - Lists: "•" bullet markers, "n." ordered markers honoring Start, Lucide square/check-square
//   glyphs for task items; nested items indent deeper than their parents.
// - Quotes: MarkdownStyles.QuoteBar bar rect, text inset past the bar, QuoteText text color;
//   nesting stacks bars and insets.
// - Thematic break: a thin rule drawn in MarkdownStyles.Rule spanning the content width.
// - Code blocks: MarkdownStyles.CodeBlockBackground box (CodeBlockBorder border), verbatim mono
//   lines; DiffContent.Syntax slot colors for a closed block with a known language; plain
//   CodeBlockText for open fences and unknown languages; content inside a HorizontalScrollView;
//   a copy button whose accessible label is the localized markdown.copy_code string and whose
//   click writes CodeBlock.Text to IClipboard.
// - TableBlock is a Step 6 placeholder: a document containing a table renders its other blocks
//   and never throws; what (if anything) the table itself draws is deliberately NOT pinned here.
public class MarkdownWidgetTests
{
    private sealed class FakeClipboard : IClipboard
    {
        public string? Text;
        public void SetText(string text) => Text = text;
        public string? GetText() => Text;
    }

    private sealed class FakeShell : IPlatformShell
    {
        public readonly List<string> OpenedUrls = new();
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) => OpenedUrls.Add(url);
    }

    private static MarkdownDocument Parse(string markdown) => new BasicMarkdownParser().Parse(markdown);

    private static (GuiTestHarness Harness, FakeClipboard Clipboard, FakeShell Shell) Create(
        string markdown, int width = 800, int height = 600, ThemeMode mode = ThemeMode.Dark)
    {
        var clipboard = new FakeClipboard();
        var shell = new FakeShell();
        var harness = GuiTestHarness.Create(
            ctx => new MarkdownWidget { Document = Parse(markdown) }.BuildView(ctx),
            width, height,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(
                    new ThemeService(new State<ThemeMode>(mode)));
                ctx.AddService<ILocalizationService>(
                    new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(clipboard);
                ctx.AddService<IPlatformShell>(shell);
            });
        return (harness, clipboard, shell);
    }

    private static ThemeStyles Dark => ThemeStyles.Dark;

    private static RecordedText Draw(RecordingCanvas canvas, string text) =>
        canvas.Texts.Single(t => t.Inputs.Text == text);

    private static RecordedText DrawContaining(RecordingCanvas canvas, string fragment) =>
        canvas.Texts.First(t => t.Inputs.Text.Contains(fragment, StringComparison.Ordinal));

    private static bool HasDrawContaining(RecordingCanvas canvas, string fragment) =>
        canvas.Texts.Any(t => t.Inputs.Text.Contains(fragment, StringComparison.Ordinal));

    private static PointF CenterOf(RecordedText t) => new(
        t.Inputs.Position.Center.X + t.TranslationX,
        t.Inputs.Position.Center.Y + t.TranslationY);

    /// <summary>The localized copy-button label. Red until the implementer adds the
    /// <c>markdown.copy_code</c> key (generated member <c>Strings.MarkdownCopyCode</c>) to all
    /// catalogs — resolved by reflection because the generated property cannot be referenced
    /// before it exists.</summary>
    private static string CopyLabel()
    {
        var property = typeof(Strings).GetProperty("MarkdownCopyCode");
        Assert.True(property != null,
            "Localization key 'markdown.copy_code' (generated member Strings.MarkdownCopyCode) is missing.");
        var value = (string?)property!.GetValue(Strings.En);
        Assert.False(string.IsNullOrWhiteSpace(value), "markdown.copy_code must have an English value.");
        return value!;
    }

    // ---------- headings ----------

    [Theory]
    [InlineData(1, 22f)]
    [InlineData(2, 16f)]
    [InlineData(3, 14f)]
    [InlineData(4, 13f)]
    [InlineData(5, 13f)]
    [InlineData(6, 13f)]
    public void HeadingRendersBoldOnTheFixedSizeLadderInStrongText(int level, float size)
    {
        var (h, _, _) = Create(new string('#', level) + " Title text");
        using (h)
        {
            var canvas = h.Render();

            var title = Draw(canvas, "Title text");
            Assert.Equal(size, title.Inputs.Style.FontSize.Value);
            Assert.Equal(FontWeight.Bold, title.Inputs.Style.FontWeight.Value);
            Assert.Equal(Dark.Palette.TextStrong, title.Inputs.Style.TextColor.Value);
        }
    }

    [Fact]
    public void HeadingInlineStylingFlowsThroughTheRunBuilder()
    {
        var (h, _, _) = Create("## Big *idea*");
        using (h)
        {
            var canvas = h.Render();

            var idea = Draw(canvas, "idea");
            Assert.Equal(MarkdownFonts.ItalicFamily, idea.Inputs.Style.FontFamily.Value);
            Assert.Equal(16f, idea.Inputs.Style.FontSize.Value);
            Assert.Equal(FontWeight.Bold, idea.Inputs.Style.FontWeight.Value);
        }
    }

    // ---------- paragraphs ----------

    [Fact]
    public void ParagraphRendersInBodySizeAndBodyColor()
    {
        var (h, _, _) = Create("Just some words");
        using (h)
        {
            var canvas = h.Render();

            var text = Draw(canvas, "Just some words");
            Assert.Equal(13f, text.Inputs.Style.FontSize.Value);
            Assert.Equal(Dark.Palette.TextBody, text.Inputs.Style.TextColor.Value);
            Assert.NotEqual(FontWeight.Bold, text.Inputs.Style.FontWeight.Value);
        }
    }

    [Fact]
    public void ParagraphBoldRunIsBoldAndItsNeighborsAreNot()
    {
        var (h, _, _) = Create("plain **strong** end");
        using (h)
        {
            var canvas = h.Render();

            Assert.Equal(FontWeight.Bold, Draw(canvas, "strong").Inputs.Style.FontWeight.Value);
            Assert.NotEqual(FontWeight.Bold, Draw(canvas, "plain ").Inputs.Style.FontWeight.Value);
        }
    }

    [Fact]
    public void ParagraphItalicRunUsesTheItalicFamily()
    {
        var (h, _, _) = Create("a *lean* word");
        using (h)
        {
            var canvas = h.Render();

            Assert.Equal(MarkdownFonts.ItalicFamily, Draw(canvas, "lean").Inputs.Style.FontFamily.Value);
            Assert.NotEqual(MarkdownFonts.ItalicFamily, Draw(canvas, "a ").Inputs.Style.FontFamily.Value);
        }
    }

    [Fact]
    public void ParagraphInlineCodeIsMonoWithAThemedChipBehindIt()
    {
        var (h, _, _) = Create("use `x = 1` now");
        using (h)
        {
            var canvas = h.Render();

            var code = Draw(canvas, "x = 1");
            Assert.Equal(DiffOptions.MonoFontFamily, code.Inputs.Style.FontFamily.Value);
            Assert.Equal(Dark.Markdown.CodeChipText, code.Inputs.Style.TextColor.Value);
            Assert.Contains(canvas.Rects,
                r => r.Inputs.Style.BackgroundColor == Dark.Markdown.CodeChipBackground);
        }
    }

    [Fact]
    public void ParagraphLinkIsUnderlinedInTheLinkColor()
    {
        var (h, _, _) = Create("see [docs](https://example.com/d) here");
        using (h)
        {
            var canvas = h.Render();

            var link = Draw(canvas, "docs");
            Assert.Equal(Dark.Markdown.Link, link.Inputs.Style.TextColor.Value);
            Assert.Contains(canvas.Lines, l => l.Inputs.Color == Dark.Markdown.Link);
        }
    }

    [Fact]
    public void ClickingALinkOpensItsUrlThroughThePlatformShell()
    {
        var (h, _, shell) = Create("see [docs](https://example.com/d) here");
        using (h)
        {
            var canvas = h.Render();
            var center = CenterOf(Draw(canvas, "docs"));

            h.Click(center.X, center.Y);

            Assert.Equal(new[] { "https://example.com/d" }, shell.OpenedUrls);
        }
    }

    [Fact]
    public void HardBreakSplitsAParagraphAcrossTwoLines()
    {
        var (h, _, _) = Create("alpha  \nbeta");
        using (h)
        {
            var canvas = h.Render();

            var first = Draw(canvas, "alpha");
            var second = Draw(canvas, "beta");
            Assert.True(second.Inputs.Position.Bottom < first.Inputs.Position.Bottom,
                "the run after a hard break must render on the next line");
        }
    }

    // ---------- lists ----------

    [Fact]
    public void UnorderedListRendersABulletPerItemLeftOfTheItemText()
    {
        var (h, _, _) = Create("- first\n- second");
        using (h)
        {
            var canvas = h.Render();

            var bullets = canvas.Texts.Where(t => t.Inputs.Text.Contains('•')).ToList();
            Assert.Equal(2, bullets.Count);
            var first = DrawContaining(canvas, "first");
            var second = DrawContaining(canvas, "second");
            Assert.All(bullets, b => Assert.True(b.Inputs.Position.Left <= first.Inputs.Position.Left));
            Assert.True(second.Inputs.Position.Bottom < first.Inputs.Position.Bottom,
                "items must stack top-down");
        }
    }

    [Fact]
    public void OrderedListNumbersItemsHonoringTheStartNumber()
    {
        var (h, _, _) = Create("3. one\n4. two");
        using (h)
        {
            var canvas = h.Render();

            Assert.True(HasDrawContaining(canvas, "3."), "the first item's marker must show 3.");
            Assert.True(HasDrawContaining(canvas, "4."), "the second item's marker must show 4.");
            Assert.True(HasDrawContaining(canvas, "one"));
            Assert.True(HasDrawContaining(canvas, "two"));
        }
    }

    [Fact]
    public void NestedListItemsIndentDeeperThanTheirParent()
    {
        var (h, _, _) = Create("- parent\n  - child");
        using (h)
        {
            var canvas = h.Render();

            var parent = DrawContaining(canvas, "parent");
            var child = DrawContaining(canvas, "child");
            Assert.True(child.Inputs.Position.Left > parent.Inputs.Position.Left,
                "nested item text must start further right than its parent's");

            var bullets = canvas.Texts.Where(t => t.Inputs.Text.Contains('•')).ToList();
            Assert.Equal(2, bullets.Count);
            Assert.True(
                bullets.Max(b => b.Inputs.Position.Left) > bullets.Min(b => b.Inputs.Position.Left),
                "the nested bullet must sit further right than the top-level bullet");
        }
    }

    [Fact]
    public void TaskListItemsRenderLucideCheckboxGlyphsMatchingTheirState()
    {
        var (h, _, _) = Create("- [x] done\n- [ ] todo");
        using (h)
        {
            var canvas = h.Render();

            var check = Draw(canvas, LucideIcons.CheckSquare);
            var box = Draw(canvas, LucideIcons.Square);
            Assert.Equal(LucideIcons.FontFamily, check.Inputs.Style.FontFamily.Value);
            Assert.Equal(LucideIcons.FontFamily, box.Inputs.Style.FontFamily.Value);

            var done = DrawContaining(canvas, "done");
            var todo = DrawContaining(canvas, "todo");
            Assert.True(check.Inputs.Position.Left < done.Inputs.Position.Left);
            Assert.True(box.Inputs.Position.Left < todo.Inputs.Position.Left);
            Assert.True(box.Inputs.Position.Bottom < check.Inputs.Position.Bottom,
                "the second item's checkbox renders below the first's");
        }
    }

    // ---------- blockquotes ----------

    [Fact]
    public void QuoteDrawsAnAccentBarAndInsetsItsTextInQuoteColor()
    {
        var (h, _, _) = Create("plain\n\n> quoted");
        using (h)
        {
            var canvas = h.Render();

            var bar = canvas.Rects.First(r => r.Inputs.Style.BackgroundColor == Dark.Markdown.QuoteBar);
            Assert.True(bar.Inputs.Position.Height > bar.Inputs.Position.Width,
                "the quote bar is a vertical accent, taller than wide");

            var plain = Draw(canvas, "plain");
            var quoted = Draw(canvas, "quoted");
            Assert.True(quoted.Inputs.Position.Left > plain.Inputs.Position.Left,
                "quoted content must be inset relative to top-level content");
            Assert.True(quoted.Inputs.Position.Left > bar.Inputs.Position.Left,
                "quoted content must sit right of its accent bar");
            Assert.Equal(Dark.Markdown.QuoteText, quoted.Inputs.Style.TextColor.Value);
        }
    }

    [Fact]
    public void NestedQuoteStacksBarsAndInsetsDeeper()
    {
        var (h, _, _) = Create("> outer\n> > deep");
        using (h)
        {
            var canvas = h.Render();

            var bars = canvas.Rects
                .Where(r => r.Inputs.Style.BackgroundColor == Dark.Markdown.QuoteBar)
                .Select(r => r.Inputs.Position.Left)
                .Distinct()
                .ToList();
            Assert.True(bars.Count >= 2, "a nested quote needs a second accent bar further right");

            var outer = Draw(canvas, "outer");
            var deep = Draw(canvas, "deep");
            Assert.True(deep.Inputs.Position.Left > outer.Inputs.Position.Left,
                "nested quote content must be inset past the outer quote's content");
        }
    }

    // ---------- thematic break ----------

    [Fact]
    public void ThematicBreakDrawsAThinThemedRuleAcrossTheContent()
    {
        var (h, _, _) = Create("above\n\n---\n\nbelow");
        using (h)
        {
            var canvas = h.Render();

            var ruleRects = canvas.Rects
                .Where(r => r.Inputs.Style.BackgroundColor == Dark.Markdown.Rule)
                .ToList();
            var ruleLines = canvas.Lines.Where(l => l.Inputs.Color == Dark.Markdown.Rule).ToList();
            Assert.True(ruleRects.Count + ruleLines.Count > 0,
                "the thematic break must draw in MarkdownStyles.Rule");

            if (ruleRects.Count > 0)
            {
                var rect = ruleRects[0];
                Assert.True(rect.Inputs.Position.Height <= 3f, "the rule is a thin line, not a band");
                Assert.True(rect.Inputs.Position.Width >= 400f, "the rule spans the content width");
            }
            else
            {
                var line = ruleLines[0];
                Assert.True(Math.Abs(line.Inputs.End.X - line.Inputs.Start.X) >= 400f,
                    "the rule spans the content width");
            }
        }
    }

    // ---------- code blocks ----------

    [Fact]
    public void CodeBlockDrawsAThemedBorderedBoxWithVerbatimMonoLines()
    {
        var (h, _, _) = Create("```\nalpha beta\ngamma\n```");
        using (h)
        {
            var canvas = h.Render();

            Assert.Contains(canvas.Rects,
                r => r.Inputs.Style.BackgroundColor == Dark.Markdown.CodeBlockBackground);
            Assert.Contains(canvas.Rects, r =>
                r.Inputs.Style.BorderColor.Top == Dark.Markdown.CodeBlockBorder ||
                r.Inputs.Style.BorderColor.Left == Dark.Markdown.CodeBlockBorder);

            var first = Draw(canvas, "alpha beta");
            var second = Draw(canvas, "gamma");
            Assert.Equal(DiffOptions.MonoFontFamily, first.Inputs.Style.FontFamily.Value);
            Assert.Equal(DiffOptions.MonoFontFamily, second.Inputs.Style.FontFamily.Value);
            Assert.Equal(Dark.Markdown.CodeBlockText, first.Inputs.Style.TextColor.Value);
            Assert.True(second.Inputs.Position.Bottom < first.Inputs.Position.Bottom,
                "code lines render in source order, top-down");
        }
    }

    [Fact]
    public void ClosedCsharpCodeBlockGetsSyntaxColoredRuns()
    {
        var (h, _, _) = Create("```csharp\nint count = 42; // note\n```");
        using (h)
        {
            var canvas = h.Render();

            var number = DrawContaining(canvas, "42");
            Assert.Equal(Dark.DiffContent.Syntax.Number, number.Inputs.Style.TextColor.Value);
            Assert.Equal(DiffOptions.MonoFontFamily, number.Inputs.Style.FontFamily.Value);

            var comment = DrawContaining(canvas, "note");
            Assert.Equal(Dark.DiffContent.Syntax.Comment, comment.Inputs.Style.TextColor.Value);
        }
    }

    [Fact]
    public void OpenFenceRendersPlainEvenWithAKnownLanguage()
    {
        // No closing fence: IsClosed is false, so highlighting must be skipped while streaming.
        var (h, _, _) = Create("```csharp\nint count = 42; // note\n");
        using (h)
        {
            var canvas = h.Render();

            var line = Draw(canvas, "int count = 42; // note");
            Assert.Equal(DiffOptions.MonoFontFamily, line.Inputs.Style.FontFamily.Value);
            Assert.Equal(Dark.Markdown.CodeBlockText, line.Inputs.Style.TextColor.Value);
            Assert.DoesNotContain(canvas.Texts,
                t => t.Inputs.Style.TextColor.Value == Dark.DiffContent.Syntax.Number);
        }
    }

    [Fact]
    public void UnknownLanguageRendersPlainMonoText()
    {
        var (h, _, _) = Create("```mysterylang\nint count = 42; // note\n```");
        using (h)
        {
            var canvas = h.Render();

            var line = Draw(canvas, "int count = 42; // note");
            Assert.Equal(DiffOptions.MonoFontFamily, line.Inputs.Style.FontFamily.Value);
            Assert.Equal(Dark.Markdown.CodeBlockText, line.Inputs.Style.TextColor.Value);
        }
    }

    [Fact]
    public void CodeBlockContentLivesInAHorizontalScrollView()
    {
        // Long lines must be scrollable sideways. Scroll physics are not pinned (the harness
        // can't meaningfully drag a scrollbar here) — the structure is: the code text sits
        // inside a HorizontalScrollView.
        var (h, _, _) = Create("```\n" + new string('x', 300) + "\n```");
        using (h)
        {
            h.Render();

            Assert.Contains(h.Root.SelfAndDescendants(), v => v is HorizontalScrollView);
        }
    }

    [Fact]
    public void CopyButtonWritesTheCodeTextToTheClipboard()
    {
        var (h, clipboard, _) = Create("```csharp\nint count = 42; // note\n```");
        using (h)
        {
            h.Render();

            h.Click(CopyLabel(), exact: false);

            Assert.Equal("int count = 42; // note", clipboard.Text);
        }
    }

    [Fact]
    public void CopyButtonCopiesVerbatimTextNotHighlightedFragments()
    {
        var source = "int a = 1; // one\nstring b = \"two\";";
        var (h, clipboard, _) = Create("```csharp\n" + source + "\n```");
        using (h)
        {
            h.Render();

            h.Click(CopyLabel(), exact: false);

            Assert.Equal(source, clipboard.Text);
        }
    }

    [Fact]
    public void MarkdownCopyCodeLocalizationKeyExists()
    {
        // markdown.copy_code must exist in the catalogs (the generator's LOC004 then forces all
        // locales). Reflection because the generated member cannot be referenced before it exists.
        var value = CopyLabel();

        Assert.NotEqual(string.Empty, value.Trim());
    }

    // ---------- tables (Step 6 placeholder) ----------

    [Fact]
    public void DocumentWithATableRendersItsOtherBlocksAndNeverThrows()
    {
        // Step 5 pin: TableBlock is a placeholder (rendered as a skipped block). Only the
        // surrounding document is pinned so Step 6 can drop in the real table without touching
        // this test.
        var (h, _, _) = Create("# Title\n\n|a|b|\n|---|---|\n|1|2|\n\nafter");
        using (h)
        {
            var canvas = h.Render();

            Assert.True(HasDrawContaining(canvas, "Title"));
            Assert.True(HasDrawContaining(canvas, "after"));
        }
    }

    // ---------- document shape ----------

    [Fact]
    public void BlocksStackTopDownInDocumentOrder()
    {
        var (h, _, _) = Create("# Head\n\nbody text");
        using (h)
        {
            var canvas = h.Render();

            var head = Draw(canvas, "Head");
            var body = Draw(canvas, "body text");
            Assert.True(body.Inputs.Position.Bottom < head.Inputs.Position.Bottom,
                "later blocks render below earlier ones");
        }
    }

    [Fact]
    public void EmptyDocumentRendersNoText()
    {
        var (h, _, _) = Create("");
        using (h)
        {
            var canvas = h.Render();

            Assert.Empty(canvas.Texts);
        }
    }
}
