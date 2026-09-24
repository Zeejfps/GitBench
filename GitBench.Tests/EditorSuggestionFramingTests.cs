using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>A suggestion arriving in the editor is brought to the middle of the viewport, even at the
/// end of the file, where only scrolling past the last line leaves room for it.</summary>
public sealed class EditorSuggestionFramingTests
{
    private const string Path = "file.cs";
    private const int Height = 600;

    private static (GuiTestHarness Harness, DiffContentView View) Show(int lineCount)
    {
        DiffContentView view = null!;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new DiffContentView(ctx);
                return view;
            },
            width: 800,
            height: Height,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new FakeClipboard());
            });
        var lines = Enumerable.Range(1, lineCount).Select(n => $"line {n}").ToArray();
        var buffer = EditorBuffer.TryOpen(
            Path,
            FilePreviewFixture.Of(lines, false),
            new FileWriteBack.Reversible(new FileEncoding(FileCharset.Utf8, LineEnding.Lf, false)),
            highlight: null,
            new LocalizationService(new State<Locale>(Locale.En)))!;
        view.SetRenderState(
            new DiffRenderState.FullFile(Path, lines, new HashSet<int>(), DiffSide.WorkingTree, Truncated: false, Emphasis: null, Annotations: null),
            buffer);
        harness.Render();
        return (harness, view);
    }

    private static float CenterOf(GuiTestHarness harness, string text) =>
        Assert.Single(harness.Render().Texts, t => t.Inputs.Text == text).Inputs.Position.Center.Y;

    [Fact]
    public void AnInsertion_LandsInTheMiddleOfTheViewport()
    {
        var (harness, view) = Show(300);

        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Insert(new FileLine(150)), ["suggested"])));

        Assert.InRange(CenterOf(harness, "suggested"), Height / 2f - 40f, Height / 2f + 40f);
    }

    [Fact]
    public void AnInsertion_AfterTheLastLine_StillLandsInTheMiddle()
    {
        var (harness, view) = Show(300);

        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Insert(new FileLine(300)), ["suggested"])));

        Assert.InRange(CenterOf(harness, "suggested"), Height / 2f - 40f, Height / 2f + 40f);
    }

    [Fact]
    public void TheCaretPlacedOnTheSuggestion_KeepsItInTheMiddle()
    {
        var (harness, view) = Show(300);
        view.SetHints(new EditorHints(Path, new EditorGhost(new GhostPlace.Replace(new FileLine(200), new FileLine(200)), ["suggested"])));
        harness.Render();

        view.RequestCaretAt(Path, TextPosition.At(200, 0));

        Assert.InRange(CenterOf(harness, "suggested"), Height / 2f - 40f, Height / 2f + 40f);
    }
}
