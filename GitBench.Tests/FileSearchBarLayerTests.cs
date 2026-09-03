using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Gui.Widgets;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The find bar floats over the file it is searching, which is only true if it composites above it.
/// <c>DrawZIndex</c> sums a view's own <c>ZIndex</c> with every ancestor's, and the body beside the
/// bar paints its rows and washes from its base z upwards — so a bar at the default 0 lays out in
/// the right place, takes the caret, reports the right hit count, and is painted over by the code.
/// Every one of those is invisible to a test that only asks the view model what it found.
/// </summary>
public class FileSearchBarLayerTests
{
    private const string Path = "src/sum.cs";

    private static readonly string[] Lines =
        Enumerable.Range(1, 60).Select(i => "var total" + i + " = " + i + ";").ToArray();

    [Fact]
    public void TheBarCompositesAboveTheCodeItFloatsOver()
    {
        var model = new FileSearchViewModel(
            () => new FilePreview.Text(Path, Lines, Truncated: false, Highlight: null),
            () => 1);

        using var harness = Harness(model, out var view);
        view.SetRenderState(new DiffRenderState.FullFile(
            Path, Lines, AddedLineNumbers: new HashSet<int>(),
            Side: DiffSide.WorkingTree, Truncated: false));

        var underneath = TopLayer(harness.Render());

        model.Open();
        model.SetText("total");
        var withBar = harness.Render();

        Assert.NotEqual(0, model.Hits.Value.Count);
        Assert.True(
            TopLayer(withBar) > underneath,
            $"the bar drew at z={TopLayer(withBar)}, no higher than the body's z={underneath}");
    }

    private static int TopLayer(RecordingCanvas canvas)
    {
        var top = 0;
        foreach (var rect in canvas.Rects) top = Math.Max(top, rect.Inputs.ZIndex);
        foreach (var text in canvas.Texts) top = Math.Max(top, text.Inputs.ZIndex);
        foreach (var run in canvas.GlyphRuns) top = Math.Max(top, run.ZIndex);
        return top;
    }

    private static GuiTestHarness Harness(FileSearchViewModel model, out DiffContentView view)
    {
        DiffContentView built = null!;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                built = new DiffContentView(ctx);
                return new Stack
                {
                    Children =
                    [
                        new Raw { View = built },
                        new Show
                        {
                            When = model.IsOpen,
                            Then = () => new FileSearchBarPlacement { Model = model },
                        },
                    ],
                }.BuildView(ctx);
            },
            width: 800,
            height: 600,
            configure: ctx =>
            {
                var mode = new State<ThemeMode>(ThemeMode.Dark);
                ctx.AddService(mode);
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(mode));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new NoClipboard());
            });
        view = built;
        return harness;
    }

    private sealed class NoClipboard : IClipboard
    {
        private string? _text;
        public void SetText(string text) => _text = text;
        public string? GetText() => _text;
    }
}
