using GitBench.Features.Diff;
using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests.Markdown;

// Smoke test over MarkdownPreviewFixture through the streaming path (MarkdownBlockList seeded via
// SetText, bound by MarkdownStream — the integration the assistant transcript uses). What is
// pinned: the fixture renders without throwing, and sentinel texts from three different construct
// families (a heading, a fenced code line, a table cell) actually reach the canvas — guarding that
// the fixture stays parseable and the streaming path stays wired to it. Per-construct visuals are
// pinned by the other Markdown suites.
public class MarkdownPreviewTests
{

    [Fact]
    public void PreviewRendersTheFixtureDocument()
    {
        // Tall enough for the whole fixture: views below the viewport are culled from the draw,
        // and a smoke test wants every construct on the canvas rather than scroll choreography.
        var list = new MarkdownBlockList(new BasicMarkdownParser());
        list.SetText(MarkdownPreviewFixture.Text);
        using var h = GuiTestHarness.Create(
            ctx => new MarkdownStream { Source = list }.BuildView(ctx),
            width: 1000, height: 6000,
            configure: ctx =>
            {
                var themeMode = new State<ThemeMode>(ThemeMode.Dark);
                ctx.AddService(themeMode);
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(themeMode));
                ctx.AddService<ILocalizationService>(
                    new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new FakeClipboard());
                ctx.AddService<IPlatformShell>(new FakeShell());
                ctx.AddService<IUiDispatcher>(new QueuedDispatcher());
                ctx.AddService<ISyntaxHighlighter>(new PlainText());
            });

        var canvas = h.Render();

        // One sentinel per construct family: heading, fenced code line, table cell. The code
        // sentinel comes from the unknown-language fence, which renders one plain mono draw per
        // line (the csharp fence is highlighted and so split into per-token draws).
        Assert.Contains(canvas.Texts,
            t => t.Inputs.Text.Contains("Markdown preview", StringComparison.Ordinal));
        Assert.Contains(canvas.Texts,
            t => t.Inputs.Text.Contains("no grammar answers to this fence", StringComparison.Ordinal));
        Assert.Contains(canvas.Texts,
            t => t.Inputs.Text.Contains("Centered", StringComparison.Ordinal));
    }
}
