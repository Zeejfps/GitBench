using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// The editor font size as a stored preference and as what code views draw at. Snapped rather than
/// rejected, like the UI scale: a throw inside <see cref="PreferencesStore.Load"/> would take every
/// other preference down with it.
/// </summary>
public sealed class EditorFontSizeTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-editor-font-prefs-");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TheDefaultMatchesTheUiBodySize()
    {
        Assert.Equal(13f, Preferences.Default.EditorFontSize.Points);
    }

    [Theory]
    [InlineData(12.4f, 12f)]
    [InlineData(17f, 16f)]
    [InlineData(2f, 10f)]
    [InlineData(400f, 24f)]
    [InlineData(float.NaN, 13f)]
    public void ASizeOffTheLadderSnapsOntoIt(float stored, float expected)
    {
        Assert.Equal(expected, new EditorFontSize(stored).Points);
    }

    [Fact]
    public void AFileWrittenBeforeTheSettingExistedReadsAsTheDefault()
    {
        var path = Path.Combine(_dir.Path, "old.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "theme": "Light",
          "uiScale": 1.5
        }
        """);

        var loaded = PreferencesStore.Load(path);

        Assert.Equal(EditorFontSize.Default, loaded.EditorFontSize);
        Assert.Equal(1.5f, loaded.UiScale.Factor);
        Assert.Equal(ThemeMode.Light, loaded.Theme);
    }

    [Fact]
    public void TheChosenSizeSurvivesASaveAndLoadWithoutTouchingTheUiScale()
    {
        var path = Path.Combine(_dir.Path, "roundtrip.json");
        var service = new PreferencesService(Preferences.Default, path);

        service.Update(p => p with { EditorFontSize = new EditorFontSize(18f) });
        service.Dispose();

        var loaded = PreferencesStore.Load(path);
        Assert.Equal(18f, loaded.EditorFontSize.Points);
        Assert.Equal(UiScale.Default, loaded.UiScale);
    }

    [Fact]
    public void ADiffViewDrawsCodeAtTheChosenSizeAndFollowsAChange()
    {
        var size = new State<EditorFontSize>(new EditorFontSize(16f));
        DiffContentView view = null!;
        using var h = GuiTestHarness.Create(
            ctx =>
            {
                view = new DiffContentView(ctx);
                return view;
            },
            width: 800,
            height: 600,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new FakeClipboard());
                ctx.AddService<IReadable<EditorFontSize>>(size);
            });

        view.SetRenderState(new DiffRenderState.Loaded(Diff()), document: null);
        h.Render();
        Assert.Equal([16f], CodeFontSizes(h.Render()));

        size.Value = new EditorFontSize(20f);

        Assert.Equal([20f], CodeFontSizes(h.Render()));
    }

    private static float[] CodeFontSizes(RecordingCanvas canvas) => canvas.Texts
        .Where(t => t.Inputs.Style.FontFamily.Value == MonoFonts.Regular)
        .Select(t => t.Inputs.Style.FontSize.Value)
        .Distinct()
        .ToArray();

    private static DiffResult Diff() => new(
        RepoId: Guid.Empty,
        Path: "file.cs",
        OldPath: null,
        Side: DiffSide.Unstaged,
        IsBinary: false,
        IsModeOnly: false,
        OldMode: null,
        NewMode: null,
        Hunks: new[]
        {
            new DiffHunk(1, 1, 1, 1, null, new[]
            {
                new DiffLine(DiffLineKind.Removed, 1, null, "old();"),
                new DiffLine(DiffLineKind.Added, null, 1, "fresh();"),
            }),
        },
        Truncated: false,
        ErrorMessage: null);
}
