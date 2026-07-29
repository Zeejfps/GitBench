using GitBench.Features.Markdown;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests.Markdown;

// Smoke test for the Step 8 dev preview (MarkdownPreviewWidget over MarkdownPreviewFixture).
// The widget is composed directly — the DIFFDINO_MARKDOWN_PREVIEW gate is AppWidget wiring, not
// under test here. What is pinned: the preview composition builds and renders the fixture without
// throwing, and sentinel texts from three different construct families (a heading, a fenced code
// line, a table cell) actually reach the canvas — guarding that the fixture stays parseable and
// the streaming path stays wired to it. Per-construct visuals are pinned by the Step 5/6/7 suites.
public class MarkdownPreviewTests
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

    [Fact]
    public void PreviewRendersTheFixtureDocument()
    {
        // Tall enough for the whole fixture: views below the viewport are culled from the draw,
        // and a smoke test wants every construct on the canvas rather than scroll choreography.
        using var h = GuiTestHarness.Create(
            ctx => new MarkdownPreviewWidget().BuildView(ctx),
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
