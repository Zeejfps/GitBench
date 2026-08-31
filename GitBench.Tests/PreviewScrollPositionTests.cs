using GitBench.Features.Diff;
using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// Where the reader is left when the file they are reading is loaded again. Both viewers are
/// reloaded on a timer — a reconcile tick, an editor save — so a reload that moves the viewport is
/// a reload the reader feels.
/// </summary>
public class PreviewScrollPositionTests
{
    private sealed class FakeClipboard : IClipboard
    {
        private string? _text;
        public void SetText(string text) => _text = text;
        public string? GetText() => _text;
    }

    private sealed class FakeShell : IPlatformShell
    {
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) { }
    }

    // A document with no document in between: the state the markdown body is put in for as long as
    // the next read takes, which is where its height — and with it the pane's offset — used to go.
    [Fact]
    public void TheMarkdownPaneHoldsItsPlaceAcrossAReloadThatBlanksTheDocument()
    {
        var text = Prose();
        var document = new State<MarkdownDocument?>(MarkdownFile.Render(text).Document);
        using var harness = MarkdownHarness(document);

        var pane = harness.Root.SelfAndDescendants().OfType<VerticalScrollPane>().Single();
        pane.Scroll(1200f);
        harness.Settle();
        var reading = pane.ScrollNormalized;
        Assert.True(reading > 0.05f, "the fixture must be tall enough to scroll");

        document.Value = null;
        harness.Settle();
        document.Value = MarkdownFile.Render(text).Document;
        harness.Settle();

        Assert.Equal(reading, pane.ScrollNormalized, 3);
    }

    [Fact]
    public void TheWholeFileViewerHoldsItsHorizontalPlaceAcrossAReload()
    {
        using var harness = DiffHarness(out var view);
        view.SetRenderState(FullFile("src/wide.cs"));
        harness.Render();
        var unscrolled = CodeLeftEdge(harness);

        view.SetHorizontalNormalizedScrollPosition(0.5f);
        harness.Render();
        var scrolled = CodeLeftEdge(harness);
        Assert.True(scrolled < unscrolled - 1f, "the fixture must be wide enough to scroll");

        view.SetRenderState(FullFile("src/wide.cs"));
        harness.Render();

        Assert.Equal(scrolled, CodeLeftEdge(harness), 1);
    }

    // The other half of the rule: it is the file that carries the offset, so a different one starts
    // at the left edge however far along the last one was read.
    [Fact]
    public void ADifferentFileStartsAtTheLeftEdge()
    {
        using var harness = DiffHarness(out var view);
        view.SetRenderState(FullFile("src/wide.cs"));
        harness.Render();
        var unscrolled = CodeLeftEdge(harness);

        view.SetHorizontalNormalizedScrollPosition(0.5f);
        harness.Render();
        Assert.True(CodeLeftEdge(harness) < unscrolled - 1f);

        view.SetRenderState(FullFile("src/other.cs"));
        harness.Render();

        Assert.Equal(unscrolled, CodeLeftEdge(harness), 1);
    }

    private static DiffRenderState.FullFile FullFile(string path) => new(
        path,
        Enumerable.Range(0, 40).Select(i => Line + i + new string('-', 400)).ToArray(),
        AddedLineNumbers: new HashSet<int>(),
        Side: DiffSide.WorkingTree,
        Truncated: false);

    private const string Line = "// a line wide enough to run well past the viewport, number ";

    /// <summary>Where the code is drawn, which is where the horizontal offset is legible from: the
    /// rows are painted at their left edge minus the offset, so scrolling right walks this left.</summary>
    private static float CodeLeftEdge(GuiTestHarness harness) =>
        harness.Canvas.Texts
            .Where(t => t.Inputs.Text.StartsWith(Line, StringComparison.Ordinal))
            .Select(t => t.Inputs.Position.Left)
            .Distinct()
            .Single();

    private static string Prose()
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++)
            text.Append("Paragraph number ").Append(i).Append(" with some words in it.\n\n");
        return text.ToString();
    }

    private static GuiTestHarness MarkdownHarness(IReadable<MarkdownDocument?> document) =>
        GuiTestHarness.Create(
            ctx => new MarkdownDocumentView
            {
                Document = Prop.Bind<MarkdownDocument?>(() => document.Value),
            }.BuildView(ctx),
            width: 800,
            height: 400,
            configure: Services);

    private static GuiTestHarness DiffHarness(out DiffContentView view)
    {
        DiffContentView built = null!;
        var harness = GuiTestHarness.Create(
            ctx => built = new DiffContentView(ctx),
            width: 800,
            height: 600,
            configure: Services);
        view = built;
        return harness;
    }

    private static void Services(Context ctx)
    {
        var mode = new State<ThemeMode>(ThemeMode.Dark);
        ctx.AddService(mode);
        ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(mode));
        ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
        ctx.AddService<IClipboard>(new FakeClipboard());
        ctx.AddService<IPlatformShell>(new FakeShell());
        ctx.AddService<IUiDispatcher>(new QueuedDispatcher());
    }
}
