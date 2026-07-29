using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Tests.Markdown;

// Drag-to-select over a RENDERED markdown document, driven through the real input system on a bare
// MarkdownStream — no transcript around it, because the selection layer belongs to the renderer and
// has to work in the dev preview too. What is pinned:
// - a drag inside one text block highlights and copies exactly that substring;
// - a drag across blocks copies the RENDERED text of each, newline-joined: no '#', no '**';
// - Ctrl+C is the copy gesture, and copies nothing when nothing is selected;
// - a press that never travels is a plain click and leaves no selection (the drag threshold), so a
//   link click still opens its url — while a drag that STARTS on a link selects instead;
// - a table contributes no text: it is one view with no per-cell leaf, so a drag across it joins
//   the blocks on either side, exactly as the diff skips its "@@" bars;
// - scope is one markdown surface: a drag that wanders into a second surface stays in the first;
// - a selection anchored in a block that re-parses is gone, while one in an untouched block lives.
//
// Geometry is the harness's synthetic measurer — 8px per UTF-16 unit, 16px lines, for every style —
// so a leaf's char offset i sits at its Position.Left + i*8. Points are derived from the laid-out
// RichTextView rather than hardcoded, since the block column's own stacking is not what's under test.
public class MarkdownSelectionTests
{
    private const float Advance = 8f;
    private const float LineH = 16f;

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

    private sealed class Surface : IDisposable
    {
        public required GuiTestHarness Harness { get; init; }
        public required MarkdownBlockList List { get; init; }
        public required FakeClipboard Clipboard { get; init; }
        public required FakeShell Shell { get; init; }
        public required ILocalizationService Localization { get; init; }

        /// <summary>The document's text leaves, in the geometric document order the selection model
        /// derives: top-down, then left-to-right (GUI coordinates are y-up).</summary>
        public List<RichTextView> Leaves =>
            Harness.Root.SelfAndDescendants().OfType<RichTextView>()
                .OrderByDescending(v => v.Position.Top)
                .ThenBy(v => v.Position.Left)
                .ToList();

        public void Dispose()
        {
            Harness.Dispose();
            List.Dispose();
        }
    }

    private static Surface Mount(string text, int width = 800, int height = 600)
    {
        MarkdownBlockList? list = null;
        var clipboard = new FakeClipboard();
        var shell = new FakeShell();
        var localization = new LocalizationService(new State<Locale>(Locale.En));
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                list = new MarkdownBlockList(new BasicMarkdownParser(), ctx.Require<IFrameTicker>());
                list.SetText(text);
                return new MarkdownStream { Source = list }.BuildView(ctx);
            },
            width, height,
            configure: ctx => Configure(ctx, clipboard, shell, localization));

        var surface = new Surface
        {
            Harness = harness,
            List = list!,
            Clipboard = clipboard,
            Shell = shell,
            Localization = localization,
        };
        harness.Render();
        return surface;
    }

    private static void Configure(
        Context ctx, IClipboard clipboard, IPlatformShell shell, ILocalizationService localization)
    {
        ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
        ctx.AddService(localization);
        ctx.AddService(clipboard);
        ctx.AddService(shell);
        ctx.AddService<IUiDispatcher>(new QueuedDispatcher());
    }

    /// <summary>The point at character offset <paramref name="charOffset"/> on
    /// <paramref name="line"/> of a leaf — the y is the line band's centre.</summary>
    private static PointF At(RichTextView leaf, int charOffset, int line = 0) =>
        new(leaf.Position.Left + charOffset * Advance, leaf.Position.Top - line * LineH - LineH / 2f);

    private static void Drag(GuiTestHarness h, PointF from, PointF to)
    {
        h.MoveTo(from.X, from.Y);
        h.Press();
        h.MoveTo(to.X, to.Y);
        h.Release();
    }

    private static IReadOnlyList<RectF> SelectionRects(RecordingCanvas canvas) =>
        RectsColored(canvas, ThemeStyles.Dark.Markdown.SelectionBackground)
            .Select(r => r.Inputs.Position)
            .ToList();

    private static List<RecordedRect> RectsColored(RecordingCanvas canvas, uint color) =>
        canvas.Rects.Where(r => r.Inputs.Style.BackgroundColor == color).ToList();

    private static string? Copy(Surface s)
    {
        s.Harness.PressKey(KeyboardKey.C, InputModifiers.Control);
        return s.Clipboard.Text;
    }

    // ---------------------------------------------------------------- inside one text block

    [Fact]
    public void DraggingInsideOneParagraphHighlightsExactlyThatSubstring()
    {
        using var s = Mount("alpha beta gamma");
        var paragraph = Assert.Single(s.Leaves);

        Drag(s.Harness, At(paragraph, 0), At(paragraph, 5)); // "alpha"

        var rect = Assert.Single(SelectionRects(s.Harness.Render()));
        Assert.Equal(paragraph.Position.Left, rect.Left, 3);
        Assert.Equal(5 * Advance, rect.Width, 3);
    }

    [Fact]
    public void DraggingInsideOneParagraphCopiesExactlyThatSubstring()
    {
        using var s = Mount("alpha beta gamma");
        var paragraph = Assert.Single(s.Leaves);

        Drag(s.Harness, At(paragraph, 6), At(paragraph, 10)); // "beta"

        Assert.Equal("beta", Copy(s));
    }

    // ---------------------------------------------------------------- across text blocks

    // The reader sees a rendered document, so a selection across it copies what they can read: the
    // heading's words without their '#', the bold word without its asterisks.
    [Fact]
    public void DraggingAcrossTwoBlocksCopiesTheRenderedTextNewlineJoined()
    {
        using var s = Mount("## Findings\n\nthe **fix** landed");
        var leaves = s.Leaves;
        Assert.Equal(2, leaves.Count);

        Drag(s.Harness, At(leaves[0], 0), At(leaves[1], "the fix landed".Length));

        Assert.Equal("Findings\nthe fix landed", Copy(s));
    }

    [Fact]
    public void DraggingAcrossTwoBlocksHighlightsBothOfThem()
    {
        using var s = Mount("## Findings\n\nthe **fix** landed");
        var leaves = s.Leaves;

        Drag(s.Harness, At(leaves[0], 0), At(leaves[1], "the fix landed".Length));

        var rects = SelectionRects(s.Harness.Render());
        Assert.True(rects.Count >= 2, $"both blocks must be highlighted; got {rects.Count} rect(s)");
        Assert.Contains(rects, r => r.Top <= leaves[0].Position.Top + 0.001f && r.Bottom >= leaves[0].Position.Bottom - 0.001f);
        Assert.Contains(rects, r => r.Top <= leaves[1].Position.Top + 0.001f && r.Bottom >= leaves[1].Position.Bottom - 0.001f);
    }

    [Fact]
    public void DraggingUpwardsSelectsTheSameSpan()
    {
        using var s = Mount("first block\n\nsecond block");
        var leaves = s.Leaves;

        Drag(s.Harness, At(leaves[1], "second block".Length), At(leaves[0], 0));

        Assert.Equal("first block\nsecond block", Copy(s));
    }

    // ---------------------------------------------------------------- the copy gesture

    [Fact]
    public void CtrlCWithNothingSelectedLeavesTheClipboardUntouched()
    {
        using var s = Mount("alpha beta gamma");
        var paragraph = Assert.Single(s.Leaves);

        var point = At(paragraph, 5);
        s.Harness.MoveTo(point.X, point.Y);

        Assert.Null(Copy(s));
    }

    // A press that never travels is a plain click: no selection is left behind, and the click still
    // belongs to whatever sits under it.
    [Fact]
    public void ClickingWithoutDraggingSelectsNothing()
    {
        using var s = Mount("alpha beta gamma");
        var paragraph = Assert.Single(s.Leaves);

        var point = At(paragraph, 5);
        s.Harness.Click(point.X, point.Y);

        Assert.Empty(SelectionRects(s.Harness.Render()));
        Assert.Null(Copy(s));
    }

    // ---------------------------------------------------------------- layering

    // An inline `code` span paints an opaque chip behind its glyphs, so a band under that chip is a
    // band nobody sees: the code reads as the one unselected word in a selected sentence. The band
    // covers every chip it runs over, the way a selection covers a background anywhere else.
    [Fact]
    public void TheSelectionBandSitsAboveInlineCodeChips()
    {
        using var s = Mount("run `git status` now");
        var paragraph = Assert.Single(s.Leaves);

        Drag(s.Harness, At(paragraph, 0), At(paragraph, "run `git status` now".Length));
        var canvas = s.Harness.Render();

        var bands = RectsColored(canvas, ThemeStyles.Dark.Markdown.SelectionBackground);
        var chips = RectsColored(canvas, ThemeStyles.Dark.Markdown.CodeChipBackground);
        Assert.NotEmpty(bands);
        Assert.NotEmpty(chips);

        var lowestBand = bands.Min(r => r.Inputs.ZIndex);
        Assert.All(chips, chip => Assert.True(
            chip.Inputs.ZIndex < lowestBand,
            $"chip at z {chip.Inputs.ZIndex} paints over a selection band at z {lowestBand}"));
    }

    // And the glyphs stay above the band that highlights them, or a selected line goes blank.
    [Fact]
    public void SelectedTextDrawsAboveItsBand()
    {
        using var s = Mount("run `git status` now");
        var paragraph = Assert.Single(s.Leaves);

        Drag(s.Harness, At(paragraph, 0), At(paragraph, "run `git status` now".Length));
        var canvas = s.Harness.Render();

        var topBand = RectsColored(canvas, ThemeStyles.Dark.Markdown.SelectionBackground)
            .Max(r => r.Inputs.ZIndex);
        Assert.All(canvas.Texts, text => Assert.True(
            text.Inputs.ZIndex > topBand,
            $"text at z {text.Inputs.ZIndex} is buried by a selection band at z {topBand}"));
    }

    // A chip covers only its own span, so the band must still run the full width of the selection
    // underneath it rather than stopping at the code.
    [Fact]
    public void TheSelectionBandRunsUnderInlineCode()
    {
        using var s = Mount("run `git status` now");
        var paragraph = Assert.Single(s.Leaves);
        var length = "run `git status` now".Length;

        Drag(s.Harness, At(paragraph, 0), At(paragraph, length));

        // One band for the whole line, not one per side of the chip — and the backticks are not in
        // the runs, so the rendered line is two characters shorter than its source.
        var band = Assert.Single(SelectionRects(s.Harness.Render()));
        Assert.Equal((length - 2) * Advance, band.Width, 3);
    }

    // A fenced block sets no chip background, so nothing can paint its band out — but the block
    // lives inside a horizontal scroll area, and its leaf has to hit-test through that all the same.
    [Fact]
    public void DraggingInsideAFencedCodeBlockHighlightsAndCopiesIt()
    {
        using var s = Mount("```\nlet x = 1\n```");
        var code = Assert.Single(s.Leaves);

        Drag(s.Harness, At(code, 0), At(code, "let x = 1".Length));

        Assert.NotEmpty(SelectionRects(s.Harness.Render()));
        Assert.Equal("let x = 1", Copy(s));
    }

    // ---------------------------------------------------------------- links

    [Fact]
    public void ClickingALinkOpensItAndStartsNoSelection()
    {
        using var s = Mount("see [docs](https://example.com) here");
        var paragraph = Assert.Single(s.Leaves);

        var point = At(paragraph, 6); // inside "docs", which spans offsets [4,8)
        s.Harness.Click(point.X, point.Y);

        Assert.Equal(new[] { "https://example.com" }, s.Shell.OpenedUrls);
        Assert.Empty(SelectionRects(s.Harness.Render()));
    }

    // The same press that opens a link on a click must start a selection once the pointer travels —
    // otherwise link text is the one prose in the document nobody can quote.
    [Fact]
    public void DraggingFromALinkSelectsInsteadOfOpeningIt()
    {
        using var s = Mount("see [docs](https://example.com) here");
        var paragraph = Assert.Single(s.Leaves);

        Drag(s.Harness, At(paragraph, 4), At(paragraph, 8)); // exactly "docs"

        Assert.Equal("docs", Copy(s));
        Assert.Empty(s.Shell.OpenedUrls);
    }

    // ---------------------------------------------------------------- tables are skipped

    // A table is one view drawing its own cells — there is no per-cell text leaf to anchor in, and
    // flattening a grid into clipboard lines is a formatting decision this version does not make.
    // So a drag across one joins the blocks on either side, the way the diff skips its "@@" bars.
    [Fact]
    public void DraggingAcrossATableCopiesOnlyTheBlocksAroundIt()
    {
        using var s = Mount("above the table\n\n| Name | Value |\n|---|---|\n| a | b |\n\nbelow the table");
        var leaves = s.Leaves;
        Assert.Equal(2, leaves.Count);

        Drag(s.Harness, At(leaves[0], 0), At(leaves[1], "below the table".Length));

        Assert.Equal("above the table\nbelow the table", Copy(s));
    }

    // ---------------------------------------------------------------- surface scope

    // Scope is one markdown surface. Two of them stacked in a column are two documents, and a drag
    // started in the first can never reach into the second — the structural guarantee that a
    // selection can never span two assistant replies.
    [Fact]
    public void ADragThatWandersIntoASecondSurfaceSelectsOnlyTheFirst()
    {
        MarkdownBlockList? first = null;
        MarkdownBlockList? second = null;
        var clipboard = new FakeClipboard();
        var shell = new FakeShell();
        var localization = new LocalizationService(new State<Locale>(Locale.En));

        using var harness = GuiTestHarness.Create(
            ctx =>
            {
                first = new MarkdownBlockList(new BasicMarkdownParser(), ctx.Require<IFrameTicker>());
                first.SetText("surface one text");
                second = new MarkdownBlockList(new BasicMarkdownParser(), ctx.Require<IFrameTicker>());
                second.SetText("surface two text");
                return new Column
                {
                    Gap = 8,
                    MainAxis = MainAxisAlignment.Start,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new MarkdownStream { Source = first },
                        new MarkdownStream { Source = second },
                    ],
                }.BuildView(ctx);
            },
            800, 600,
            configure: ctx => Configure(ctx, clipboard, shell, localization));
        harness.Render();

        var leaves = harness.Root.SelfAndDescendants().OfType<RichTextView>()
            .OrderByDescending(v => v.Position.Top).ToList();
        Assert.Equal(2, leaves.Count);

        Drag(harness, At(leaves[0], 0), At(leaves[1], "surface two text".Length));
        harness.PressKey(KeyboardKey.C, InputModifiers.Control);
        first!.Dispose();
        second!.Dispose();

        Assert.Equal("surface one text", clipboard.Text);
    }

    // ---------------------------------------------------------------- re-parse

    // A streaming delta can turn the block a selection is anchored in into a different block
    // entirely — a paragraph becomes a table the moment its delimiter row arrives. The anchor's
    // leaf is rebuilt, so the selection is gone rather than pointing at text that moved.
    [Fact]
    public void ReparsingTheAnchoringBlockClearsTheSelection()
    {
        using var s = Mount("intro paragraph\n\n| Name | Value |");
        var tail = s.Leaves[1];

        Drag(s.Harness, At(tail, 0), At(tail, "| Name | Value |".Length));
        Assert.NotEmpty(SelectionRects(s.Harness.Render()));

        s.List.SetText("intro paragraph\n\n| Name | Value |\n|---|---|");
        s.Harness.Render();

        Assert.Empty(SelectionRects(s.Harness.Render()));
        Assert.Null(Copy(s));
    }

    [Fact]
    public void GrowingTheAnchoringBlockClearsTheSelection()
    {
        using var s = Mount("intro paragraph\n\ntail");
        var tail = s.Leaves[1];

        Drag(s.Harness, At(tail, 0), At(tail, 4));
        Assert.NotEmpty(SelectionRects(s.Harness.Render()));

        s.List.SetText("intro paragraph\n\ntail keeps coming");
        s.Harness.Render();

        Assert.Empty(SelectionRects(s.Harness.Render()));
        Assert.Null(Copy(s));
    }

    // The converse, and the reason identity is the leaf rather than an index: a block the delta
    // never touched keeps its view, so a selection inside it survives the tail streaming on.
    [Fact]
    public void ASelectionInAnUntouchedBlockSurvivesATailDelta()
    {
        using var s = Mount("intro paragraph\n\ntail");
        var intro = s.Leaves[0];

        Drag(s.Harness, At(intro, 0), At(intro, "intro".Length));

        s.List.SetText("intro paragraph\n\ntail keeps coming");
        s.Harness.Render();

        var point = At(intro, 3);
        s.Harness.MoveTo(point.X, point.Y);

        Assert.NotEmpty(SelectionRects(s.Harness.Render()));
        Assert.Equal("intro", Copy(s));
    }

    // ---------------------------------------------------------------- right-click

    [Fact]
    public void RightClickOverALiveSelectionOffersCopy()
    {
        using var s = Mount("alpha beta gamma");
        var paragraph = Assert.Single(s.Leaves);

        Drag(s.Harness, At(paragraph, 0), At(paragraph, 5));
        var over = At(paragraph, 3);
        s.Harness.Click(over.X, over.Y, MouseButton.Right);

        Assert.Equal(1, s.Harness.OpenMenuCount);
        s.Harness.ClickMenuItem(s.Localization.Strings.Value.CommonCopy);

        // Picking the item dismisses the menu and only then runs what it stands for — the item's
        // action is deferred past the menu's teardown, so it never fires behind a still-open popup.
        Assert.Equal(0, s.Harness.OpenMenuCount);
        Assert.Equal("alpha", s.Clipboard.Text);
    }
}
