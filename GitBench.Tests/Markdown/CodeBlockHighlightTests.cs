using GitBench.Features.Diff;
using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests.Markdown;

// Where a code block's tokenizing runs, and how often. The block list feeds MarkdownStream exactly
// as a streamed assistant turn would, and a counting ISyntaxHighlighter (delegating to the shared
// real one, so the colors are the real ones) records every pass and the thread it ran on.
//
// Pinned contracts:
// - a build never tokenizes on the UI thread: the first frame after a closed fence arrives is
//   still plain, and the pass runs on some other thread;
// - an open (still streaming) fence is never tokenized at all, however many chunks it grows by;
// - a fence that closes later is tokenized exactly once, and stays at once as the stream
//   continues past it;
// - a theme flip re-resolves span colors without a second tokenize pass.
public class CodeBlockHighlightTests
{
    private sealed class CountingHighlighter : ISyntaxHighlighter
    {
        private int _calls;

        public int CallThreadId;
        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, string languageId)
        {
            CallThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref _calls);
            return SyntaxHighlighter.Shared.Highlight(fileText, languageId);
        }
    }

    private sealed record Fixture(
        GuiTestHarness Harness,
        MarkdownBlockList List,
        CountingHighlighter Highlighter,
        QueuedDispatcher Dispatcher,
        State<ThemeMode> Mode);

    private static Fixture Create()
    {
        var highlighter = new CountingHighlighter();
        var dispatcher = new QueuedDispatcher();
        var mode = new State<ThemeMode>(ThemeMode.Dark);
        MarkdownBlockList? list = null;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                list = new MarkdownBlockList(new BasicMarkdownParser(), ctx.Require<IFrameTicker>());
                return new MarkdownStream { Source = list }.BuildView(ctx);
            },
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(mode));
                ctx.AddService<ILocalizationService>(
                    new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<ISyntaxHighlighter>(highlighter);
                ctx.AddService<IUiDispatcher>(dispatcher);
            });
        return new Fixture(harness, list!, highlighter, dispatcher, mode);
    }

    private const string ClosedBlock = "```csharp\nint count = 42; // note\n```";
    private const string OpenBlock = "```csharp\nint count = 42; // note\n";

    private static ThemeStyles Dark => ThemeStyles.Dark;
    private static ThemeStyles Light => ThemeStyles.Light;

    private static uint ColorOf(RecordingCanvas canvas, string fragment) =>
        canvas.Texts.First(t => t.Inputs.Text.Contains(fragment, StringComparison.Ordinal))
            .Inputs.Style.TextColor.Value;

    // The pass runs on a worker and posts its spans back afterwards, so waiting on the call itself
    // races the post. What these tests read is the color on the screen; that is what is waited for.
    private static void AwaitTokenize(Fixture f) =>
        Pump.WaitFor(
            f.Dispatcher,
            () => ColorOf(f.Harness.Render(), "42") != Dark.Markdown.CodeBlockText,
            "the tokenized spans to reach the screen");

    [Fact]
    public void BuildDoesNotTokenizeOnTheUiThread()
    {
        var f = Create();
        using (f.Harness)
        {
            f.List.SetText(ClosedBlock);

            // Nothing has been posted back yet, so the freshly built block still paints plain —
            // a synchronous tokenize inside Build would already have colored it.
            var first = f.Harness.Render();
            Assert.Equal(Dark.Markdown.CodeBlockText, ColorOf(first, "42"));

            AwaitTokenize(f);

            Assert.NotEqual(Environment.CurrentManagedThreadId, f.Highlighter.CallThreadId);
            Assert.Equal(Dark.DiffContent.Syntax.Number, ColorOf(f.Harness.Render(), "42"));
        }
    }

    [Fact]
    public void OpenFenceIsNeverTokenized()
    {
        var f = Create();
        using (f.Harness)
        {
            f.List.SetText("```csharp\nint");
            f.Harness.Render();
            f.List.SetText("```csharp\nint count");
            f.Harness.Render();
            f.List.SetText(OpenBlock);
            f.Harness.Render();
            f.Dispatcher.Drain();

            Assert.Equal(0, f.Highlighter.Calls);
            Assert.Equal(Dark.Markdown.CodeBlockText, ColorOf(f.Harness.Render(), "42"));
        }
    }

    [Fact]
    public void FenceThatClosesLaterIsTokenizedExactlyOnce()
    {
        var f = Create();
        using (f.Harness)
        {
            f.List.SetText(OpenBlock);
            f.Harness.Render();
            Assert.Equal(0, f.Highlighter.Calls);

            f.List.SetText(ClosedBlock);
            f.Harness.Render();
            AwaitTokenize(f);
            Assert.Equal(Dark.DiffContent.Syntax.Number, ColorOf(f.Harness.Render(), "42"));

            // The stream continues past the settled block: its slot no longer changes, so it must
            // not be tokenized again.
            f.List.SetText(ClosedBlock + "\n\nafter the fence");
            f.Harness.Render();
            f.Dispatcher.Drain();

            Assert.Equal(1, f.Highlighter.Calls);
        }
    }

    [Fact]
    public void ThemeFlipRecolorsWithoutRetokenizing()
    {
        var f = Create();
        using (f.Harness)
        {
            f.List.SetText(ClosedBlock);
            f.Harness.Render();
            AwaitTokenize(f);
            Assert.Equal(Dark.DiffContent.Syntax.Number, ColorOf(f.Harness.Render(), "42"));

            f.Mode.Value = ThemeMode.Light;
            var canvas = f.Harness.Render();
            f.Dispatcher.Drain();

            Assert.Equal(Light.DiffContent.Syntax.Number, ColorOf(canvas, "42"));
            Assert.Equal(1, f.Highlighter.Calls);
        }
    }
}
