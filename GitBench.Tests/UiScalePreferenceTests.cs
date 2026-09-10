using GitBench.App;
using GitBench.Localization;
using GitBench.Theming;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The UI scale as a stored preference. A value off the ladder is snapped onto it rather than
/// rejected: a throw inside <see cref="PreferencesStore.Load"/> is caught wholesale and would take
/// every other preference down with it.
/// </summary>
public sealed class UiScalePreferenceTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-ui-scale-prefs-");

    public void Dispose() => _dir.Dispose();

    private string WriteFile(string uiScaleJson)
    {
        var path = Path.Combine(_dir.Path, "prefs.json");
        File.WriteAllText(path, $$"""
        {
          "schemaVersion": 1,
          "theme": "Light",
          "language": "Ja",
          "uiScale": {{uiScaleJson}},
          "windowWidth": 1234,
          "repoBarCollapsed": true
        }
        """);
        return path;
    }

    [Fact]
    public void TheDefaultIsUnscaled()
    {
        Assert.Equal(1f, Preferences.Default.UiScale.Factor);
    }

    [Fact]
    public void AStoredScaleIsRestored()
    {
        var loaded = PreferencesStore.Load(WriteFile("1.5"));

        Assert.Equal(1.5f, loaded.UiScale.Factor);
    }

    [Theory]
    [InlineData("1.03", 1f)]
    [InlineData("40", 2f)]
    [InlineData("-3", 0.8f)]
    [InlineData("0", 0.8f)]
    public void AScaleOffTheLadderSnapsOntoIt(string stored, float expected)
    {
        var loaded = PreferencesStore.Load(WriteFile(stored));

        Assert.Equal(expected, loaded.UiScale.Factor);
    }

    [Fact]
    public void AnAbsentScaleLeavesEveryOtherPreferenceIntact()
    {
        var loaded = PreferencesStore.Load(WriteFile("null"));

        Assert.Equal(1f, loaded.UiScale.Factor);
        Assert.Equal(ThemeMode.Light, loaded.Theme);
        Assert.Equal(Locale.Ja, loaded.Language);
        Assert.Equal(1234, loaded.WindowWidth);
        Assert.True(loaded.RepoBarCollapsed);
    }

    [Fact]
    public void AnOutOfRangeScaleLeavesEveryOtherPreferenceIntact()
    {
        var loaded = PreferencesStore.Load(WriteFile("999"));

        Assert.Equal(2f, loaded.UiScale.Factor);
        Assert.Equal(ThemeMode.Light, loaded.Theme);
        Assert.Equal(Locale.Ja, loaded.Language);
        Assert.Equal(1234, loaded.WindowWidth);
        Assert.True(loaded.RepoBarCollapsed);
    }

    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 2f)]
    [InlineData(float.NegativeInfinity, 0.8f)]
    public void NoFloatIsUnrepresentableAsAScale(float value, float expected)
    {
        Assert.Equal(expected, new UiScale(value).Factor);
    }

    [Fact]
    public void EveryRungIsOnTheLadderItSnapsTo()
    {
        foreach (var rung in UiScale.All)
            Assert.Equal(rung, new UiScale(rung.Factor));
    }

    [Fact]
    public void ARungIsLabelledAsAPercentage()
    {
        Assert.Equal("125%", new UiScale(1.25f).Label);
        Assert.Equal("100%", UiScale.Default.Label);
    }

    [Fact]
    public void AFileWrittenBeforeTheSettingExistedReadsAsUnscaled()
    {
        var path = Path.Combine(_dir.Path, "old.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "theme": "Light"
        }
        """);

        var loaded = PreferencesStore.Load(path);

        Assert.Equal(1f, loaded.UiScale.Factor);
        Assert.Equal(ThemeMode.Light, loaded.Theme);
    }

    [Fact]
    public void TheChosenScaleSurvivesASaveAndLoad()
    {
        var path = Path.Combine(_dir.Path, "roundtrip.json");
        var service = new PreferencesService(Preferences.Default, path);

        service.SetUiScale(new UiScale(1.25f));
        service.Dispose();

        Assert.Equal(1.25f, PreferencesStore.Load(path).UiScale.Factor);
    }
}
