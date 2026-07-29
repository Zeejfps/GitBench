using GitBench.Features.Diff;
using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests.Markdown;

// Harness tests for MarkdownStream, the Each-bound streaming surface over MarkdownBlockList.
// What is pinned here:
// - the rendered output always reflects the list's current blocks (content presence only —
//   per-block visuals are pinned by the Step 5/6 suites);
// - VIEW SURVIVAL: a completed block's view instance stays in the tree when later text streams
//   in. This is observable through the public harness surface: Each's children binding only
//   rebuilds slots that receive list events, so an untouched slot's view must remain
//   reference-identical in Root.SelfAndDescendants(). RichTextView (one per paragraph/heading)
//   is the identity probe;
// - in-progress constructs render as their eventual shape (open fence -> live code block,
//   header+delimiter-only table -> table);
// - the throttled path integrates with the harness frame ticker: SetTextThrottled renders
//   nothing until the tick, everything after it.
//
// The list is built against the harness's IFrameTicker (the same service the real transcript
// would hand it), so h.Tick drives the throttle exactly like a live frame.
public class MarkdownStreamTests
{
    private sealed class FakeClipboard : IClipboard
    {
        public string? Text;
        public void SetText(string text) => Text = text;
        public string? GetText() => Text;
    }

    private sealed class FakeShell : IPlatformShell
    {
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) { }
    }

    private static (GuiTestHarness Harness, MarkdownBlockList List) Create(
        int width = 800, int height = 600)
    {
        MarkdownBlockList? list = null;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                list = new MarkdownBlockList(
                    new BasicMarkdownParser(), ctx.Require<IFrameTicker>());
                return new MarkdownStream { Source = list }.BuildView(ctx);
            },
            width, height,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(
                    new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(
                    new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new FakeClipboard());
                ctx.AddService<IPlatformShell>(new FakeShell());
                ctx.AddService<IUiDispatcher>(new QueuedDispatcher());
            });
        return (harness, list!);
    }

    private static ThemeStyles Dark => ThemeStyles.Dark;

    private static bool HasDraw(RecordingCanvas canvas, string fragment) =>
        canvas.Texts.Any(t => t.Inputs.Text.Contains(fragment, StringComparison.Ordinal));

    private static List<RichTextView> RichTextViews(GuiTestHarness h) =>
        h.Root.SelfAndDescendants().OfType<RichTextView>().ToList();

    // ------------------------------------------------------------------------ rendered output

    [Fact]
    public void EmptySourceRendersNoText()
    {
        var (h, _) = Create();
        using (h)
        {
            var canvas = h.Render();

            Assert.Empty(canvas.Texts);
        }
    }

    [Fact]
    public void RendersTheCurrentBlocks()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetText("# Hello\n\nstreamed body");

            var canvas = h.Render();

            Assert.True(HasDraw(canvas, "Hello"));
            Assert.True(HasDraw(canvas, "streamed body"));
        }
    }

    [Fact]
    public void AppendedBlockAppearsInTheRender()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetText("first paragraph");
            h.Render();

            list.SetText("first paragraph\n\nsecond paragraph");
            var canvas = h.Render();

            Assert.True(HasDraw(canvas, "first paragraph"));
            Assert.True(HasDraw(canvas, "second paragraph"));
        }
    }

    [Fact]
    public void GrowingParagraphRendersTheLatestTextOnly()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetText("stream");
            h.Render();

            list.SetText("streaming update");
            var canvas = h.Render();

            Assert.True(HasDraw(canvas, "streaming update"));
            Assert.DoesNotContain(canvas.Texts, t => t.Inputs.Text == "stream");
        }
    }

    [Fact]
    public void RetractedBlockDisappearsFromTheRender()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetText("keep\n\ndrop");
            h.Render();

            list.SetText("keep");
            var canvas = h.Render();

            Assert.True(HasDraw(canvas, "keep"));
            Assert.False(HasDraw(canvas, "drop"));
        }
    }

    // ------------------------------------------------------------------------- view survival

    [Fact]
    public void CompletedBlockViewSurvivesAppendOfANewBlock()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetText("completed paragraph");
            h.Render();
            var block0View = Assert.Single(RichTextViews(h));

            list.SetText("completed paragraph\n\n# Streamed heading");
            h.Render();

            var views = RichTextViews(h);
            Assert.Equal(2, views.Count);
            Assert.Contains(views, v => ReferenceEquals(v, block0View));
        }
    }

    [Fact]
    public void CompletedBlockViewSurvivesGrowthOfTheLastBlock()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetText("completed paragraph\n\ntail");
            h.Render();
            // Views come back in document order: index 0 is block 0's view.
            var views = RichTextViews(h);
            Assert.Equal(2, views.Count);
            var block0View = views[0];

            list.SetText("completed paragraph\n\ntail grows longer");
            h.Render();

            var after = RichTextViews(h);
            Assert.Equal(2, after.Count);
            Assert.Same(block0View, after[0]);
        }
    }

    [Fact]
    public void CompletedBlockViewSurvivesAWholeStreamedTail()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetText("intro paragraph\n\nx");
            h.Render();
            var block0View = RichTextViews(h)[0];

            // Stream a growing tail through several construct changes; block 0 never changes,
            // so its view must never be rebuilt.
            list.SetText("intro paragraph\n\nxy grows");
            h.Render();
            list.SetText("intro paragraph\n\nxy grows\n\n- a list\n- appears");
            h.Render();
            list.SetText("intro paragraph\n\nxy grows\n\n- a list\n- appears\n\n```csharp\nvar z = 1;");
            var canvas = h.Render();

            Assert.Same(block0View, RichTextViews(h)[0]);
            Assert.True(HasDraw(canvas, "intro paragraph"));
            Assert.True(HasDraw(canvas, "var z = 1;"));
        }
    }

    // ------------------------------------------------------------- in-progress constructs

    [Fact]
    public void OpenFenceRendersAsALiveCodeBlock()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetText("```csharp\nint x = 1;");

            var canvas = h.Render();

            var line = canvas.Texts.Single(t => t.Inputs.Text == "int x = 1;");
            Assert.Equal(DiffOptions.MonoFontFamily, line.Inputs.Style.FontFamily.Value);
            Assert.Contains(canvas.Rects,
                r => r.Inputs.Style.BackgroundColor == Dark.Markdown.CodeBlockBackground);
        }
    }

    [Fact]
    public void HeaderAndDelimiterOnlyRendersAsATable()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetText("| Name | Value |\n|---|---|");

            var canvas = h.Render();

            Assert.True(HasDraw(canvas, "Name"));
            Assert.True(HasDraw(canvas, "Value"));
        }
    }

    // ------------------------------------------------------------------------ throttled path

    [Fact]
    public void ThrottledTextAppearsOnlyAfterTheFrameTick()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetTextThrottled("throttled words");

            var before = h.Render();
            Assert.False(HasDraw(before, "throttled words"));

            h.Tick(1f / 30f);

            var after = h.Render();
            Assert.True(HasDraw(after, "throttled words"));
        }
    }

    [Fact]
    public void ThrottledUpdatePreservesCompletedBlockViews()
    {
        var (h, list) = Create();
        using (h)
        {
            list.SetText("stable intro\n\ntail");
            h.Render();
            var block0View = RichTextViews(h)[0];

            list.SetTextThrottled("stable intro\n\ntail keeps growing");
            h.Tick(1f / 30f);
            var canvas = h.Render();

            Assert.Same(block0View, RichTextViews(h)[0]);
            Assert.True(HasDraw(canvas, "tail keeps growing"));
        }
    }
}
